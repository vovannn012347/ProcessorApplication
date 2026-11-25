using System.Collections.Concurrent;
using System.Data;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

using Common.Code;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using P2PModule.Configuration;
using P2PModule.Interfaces;
using P2PModule.Models;
using P2PModule.Models.Network;
using P2PModule.Services.Gossip;
using P2PModule.Services.Persistence;
using P2PModule.Services.Rating;

using SIPSorcery.Net;

using static P2PModule.Services.Connection.PeerConnectionManager;
using ProcessorApplication.Utils;

namespace P2PModule.Services.Connection;

public interface IPeerConnectionManager
{
    event Action<string, string, P2PPacketEnvelope> OnRawMessageReceived;
    /// <summary>
    /// Raised when a remote peer opens an unmanaged data channel.
    /// Consumers (e.g. Torrent Engine, File Streamers) subscribe to handle raw RTCDataChannel streams.
    /// Parameters: (senderNodeId, cleanChannelLabel, rtcDataChannel)
    /// </summary>
    event Action<string, string, RTCDataChannel> OnUnmanagedChannelReceived;

    //Task<ActivePeerConnection?> GetConnection(string nodeId);
    ActivePeerConnection? GetConnection(string nodeId);
    Task<ActivePeerConnection?> ConnectPeerAsync(string nodeId, string[] signalingEndpoints, string connectionType);
    Task<bool> KillConnectionAsync(string nodeId, string terminationReason = "");
    IEnumerable<ActivePeerConnection> GetActiveConnections();


    Task<CreateChannelResult> CreateManagedDataChannelAsync(string nodeId, string channelLabel);
    Task<CreateChannelResult> CreateUnmanagedDataChannelAsync(string nodeId, string channelLabel);

    bool SendMessageRaw(string nodeId, string payload, string channelLabel = "p2p-control-channel");
    /// <summary>For answers: sends over the channel the node's last message arrived on, if it is open; else like SendMessageRaw.</summary>
    bool SendReplyRaw(string nodeId, string payload, string channelLabel = "p2p-control-channel");
    /// <summary>True if anything arrived from the node within the given time (any channel).</summary>
    bool HeardFromRecently(string nodeId, TimeSpan within);
    /// <summary>
    /// The link to the node is dead (heartbeats failed): drop it and get ONE new connection between the two nodes.
    /// The node with the smaller id dials; the other waits for that dial and dials itself only if nothing comes.
    /// </summary>
    Task RecoverDeadLinkAsync(string nodeId, string[] signalingEndpoints);
    /// <summary>Answers a link-resume request (see /p2p/link/resume); null = not accepted.</summary>
    LinkResumeResponse? HandleLinkResume(LinkResumeRequest request);
    /// <summary>
    /// The control channel to the node isn't delivering: open a new one on the same peer connection and switch to it.
    /// The other node switches too when the new channel reaches it. Sends to the node wait while this runs.
    /// </summary>
    Task<bool> RestartControlChannelAsync(string nodeId);
    /// <summary>Waits (up to max) while the node's control channel is being recreated; true if it can be sent on now.</summary>
    Task<bool> WaitUntilSendableAsync(string nodeId, string channelLabel, TimeSpan max);
    /// <summary>
    /// True while the node is known to be down (its endpoints refuse connections, or it could not be reached at
    /// all). Callers should report "offline" at once instead of retrying, re-dialling or routing through relays.
    /// Cleared as soon as anything arrives from the node.
    /// </summary>
    bool IsOffline(string nodeId);
    Task<string?> HandleIncomingSignalingHttpAsync(string senderNodeId, string offerSdp, Guid handshakeId);
    void HandleRelaySignaling(string senderNodeId, P2PPacketEnvelope frame);
    Task<string> GetEndpointActiveAsync(string endpoint);
}

public class PeerConnectionManager : IPeerConnectionManager, IDisposable
{
    public const string ControlChannel = "p2p-control-channel";
    public const string TimesyncChannel = "p2p-timesync-channel";

    public class CreateChannelResult
    {
        public RTCDataChannel? Channel { get; set; } = null;

        // Was inverted (IsNullOrEmpty): every successfully created channel reported an error.
        public bool IsError => !string.IsNullOrEmpty(Error);
        public string Error { get; set; } = string.Empty;
    }

    private readonly ILogger<PeerConnectionManager> _logger;
    private readonly IOptionsMonitor<P2PSettings> _settings;
    private readonly HttpClient _http;
    private readonly INodeIdentityService _identityService;
    private readonly IPeerTracker _peerTracker;
    private readonly IRatingService _ratingService;
    private readonly IChallengeService _challengeService;

    private readonly Timer _handshakeTimeoutTimer;
    private IDisposable? _settingsChangeListener;
    private readonly Timer _timeSyncPulseTimer; 
    
    private readonly ConcurrentDictionary<string, ActivePeerConnection> _connections = new();
    /// <summary>One dial per remote node at a time (see ConnectPeerAsync).</summary>
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _dialLocks = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>
    /// The channel each node's messages last ARRIVED on, per label ("node|label"). After two dials crossed, the
    /// connection stored for a node can be a different (dead) one than the one the node still talks to us over:
    /// its requests kept arriving while every answer went to the dead connection ("channel not open") and its
    /// heartbeats failed for good. SendMessageRaw falls back to this channel then.
    /// </summary>
    private readonly ConcurrentDictionary<string, RTCDataChannel> _lastInboundChannel = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>When anything last arrived from each node, on any channel.</summary>
    private readonly ConcurrentDictionary<string, DateTime> _lastInboundUtc = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>A connection that has carried nothing from the node for this long may be replaced by a new dial.</summary>
    private static readonly TimeSpan SilentConnectionLimit = TimeSpan.FromSeconds(20);
    // ---- Nodes that are down ----------------------------------------------------------------------------------
    private readonly ConcurrentDictionary<string, (DateTime Until, int Strikes)> _offline = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, bool> _endpointRefused = new(StringComparer.OrdinalIgnoreCase);

    public bool IsOffline(string nodeId) =>
        _offline.TryGetValue(nodeId, out var o) && o.Until > DateTime.UtcNow;

    /// <summary>Back-off grows 15 s, 30 s, 60 s ... up to 5 min while the node stays down.</summary>
    private void MarkOffline(string nodeId, string reason)
    {
        var strikes = _offline.TryGetValue(nodeId, out var o) ? o.Strikes + 1 : 1;
        var backoff = TimeSpan.FromSeconds(Math.Min(300, 15 * Math.Pow(2, Math.Min(strikes - 1, 5))));
        _offline[nodeId] = (DateTime.UtcNow + backoff, strikes);
        _logger.LogWarning("[P2P Transport] {NodeId} is offline ({Reason}). Not dialling or routing to it for {Seconds}s.", nodeId, reason, (int)backoff.TotalSeconds);
    }

    private void MarkOnline(string nodeId)
    {
        if (_offline.TryRemove(nodeId, out _))
            _logger.LogInformation("[P2P Transport] {NodeId} is reachable again.", nodeId);
    }

    /// <summary>Control-channel restarts in progress per node; sends to that node wait on these.</summary>
    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _channelRestarts = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan ChannelOpenWait = TimeSpan.FromSeconds(8);

    public async Task<bool> RestartControlChannelAsync(string nodeId)
    {
        if (!_connections.TryGetValue(nodeId, out var conn) || conn.PeerConnection?.connectionState != RTCPeerConnectionState.connected)
            return false;

        var mine = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_channelRestarts.TryAdd(nodeId, mine))
        {
            // already being recreated (by us): wait for that one
            var running = _channelRestarts.TryGetValue(nodeId, out var r) ? r.Task : Task.FromResult(false);
            return await Task.WhenAny(running, Task.Delay(ChannelOpenWait)) == running && running.Result;
        }

        bool ok = false;
        try
        {
            conn.DataChannels.TryGetValue(ControlChannel, out var old);
            _logger.LogWarning("[P2P Transport] Control channel to {NodeId} is not delivering; opening a new one on the same connection (sends to it wait meanwhile).", nodeId);

            var dc = await conn.PeerConnection.createDataChannel(ControlChannel);
            if (dc == null) return false;
            ForgetInbound(nodeId);
            RegisterAndBindDataChannel(nodeId, dc);   // becomes conn.DataChannels[control]; same message handling

            var until = DateTime.UtcNow + ChannelOpenWait;
            while (dc.readyState != RTCDataChannelState.open && DateTime.UtcNow < until)
                await Task.Delay(100);

            ok = dc.readyState == RTCDataChannelState.open;
            if (ok)
            {
                if (old != null && !ReferenceEquals(old, dc)) { try { old.close(); } catch { } }
                _logger.LogInformation("[P2P Transport] New control channel to {NodeId} is open; queued sends continue on it.", nodeId);
            }
            else
            {
                _logger.LogWarning("[P2P Transport] New control channel to {NodeId} did not open within {Seconds}s; the connection itself is dead.", nodeId, (int)ChannelOpenWait.TotalSeconds);
            }
            return ok;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[P2P Transport] Could not recreate the control channel to {NodeId}.", nodeId);
            return false;
        }
        finally
        {
            _channelRestarts.TryRemove(nodeId, out _);
            mine.TrySetResult(ok);
        }
    }

    public async Task<bool> WaitUntilSendableAsync(string nodeId, string channelLabel, TimeSpan max)
    {
        if (_channelRestarts.TryGetValue(nodeId, out var restart))
            await Task.WhenAny(restart.Task, Task.Delay(max));

        return _connections.TryGetValue(nodeId, out var conn) &&
               conn.DataChannels.TryGetValue(channelLabel, out var dc) &&
               dc.readyState == RTCDataChannelState.open;
    }

    /// <summary>Nodes we are dialling right now (outbound offer not answered yet).</summary>
    private readonly ConcurrentDictionary<string, byte> _dialsInProgress = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Link key per node: the handshake id both sides used for the last connection that came up between them,
    /// kept for LinkKeyLifetime. On a reconnect the two nodes prove to each other - each signing with its
    /// own private key, checked against the other's known public key - that they still hold the SAME key;
    /// then the connection is rebuilt on it without a full double-blind handshake. If either side has lost
    /// it, it expired, or the signatures don't check out, the normal handshake runs.
    /// </summary>
    private readonly ConcurrentDictionary<string, LinkKey> _linkKeys = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan LinkKeyLifetime = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan LinkResumeClockSkew = TimeSpan.FromSeconds(60);
    private sealed record LinkKey(Guid Id, DateTime ExpiresUtc);
    /// <summary>Signed resumes we accepted: the following WebRTC offer may use the link key (once, within 30 s).</summary>
    private readonly ConcurrentDictionary<string, LinkKey> _resumeApprovals = new(StringComparer.OrdinalIgnoreCase);

    private bool ConsumeResumeApproval(string nodeId, Guid id) =>
        _resumeApprovals.TryGetValue(nodeId, out var approval) && approval.Id == id && approval.ExpiresUtc > DateTime.UtcNow &&
        _resumeApprovals.TryRemove(nodeId, out _);

    private void RememberLinkKey(string nodeId, Guid handshakeId)
    {
        if (handshakeId == Guid.Empty) return;
        _linkKeys[nodeId] = new LinkKey(handshakeId, DateTime.UtcNow + LinkKeyLifetime);
    }

    private bool HasLinkKey(string nodeId, Guid id) =>
        _linkKeys.TryGetValue(nodeId, out var key) && key.Id == id && key.ExpiresUtc > DateTime.UtcNow;

    // ---- Link resume, encrypted end to end ------------------------------------------------------------------
    // Both nodes exchanged their public keys in the first handshake, so a reconnect can be done like TLS:
    //   request : Encrypt(peer's public ENCRYPTION key, [linkKey | nonceA | time]) + Sign(our private SIGNATURE key)
    //   answer  : Encrypt(our public ENCRYPTION key,  [nonceA | nonceB])            + Sign(its private SIGNATURE key)
    // Only the real peer can decrypt (private key), only the real peer can sign; the old link key never travels
    // in clear text. Both then switch to a NEW link key derived from (old key, nonceA, nonceB), which is the
    // handshake id of the reconnect - so every reconnect rotates it.

    private const int LinkPayloadSize = 64;   // multiple of the AES block size (P2PCrypto.Encrypt pads nothing)

    private static string LinkResumeText(string signer, string other, long ticks, byte[] envelope) =>
        $"p2p-link-resume|{signer}|{other}|{ticks}|{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(envelope))}";

    private static Guid NextLinkKey(Guid oldKey, byte[] nonceA, byte[] nonceB)
    {
        var material = oldKey.ToByteArray().Concat(nonceA).Concat(nonceB).ToArray();
        return new Guid(System.Security.Cryptography.SHA256.HashData(material).AsSpan(0, 16));
    }

    public LinkResumeResponse? HandleLinkResume(LinkResumeRequest request)
    {
        try
        {
            if (request == null || string.IsNullOrEmpty(request.SenderNodeId) || request.Signature == null || request.Envelope == null) return null;
            if (!string.Equals(request.TargetNodeId, _identityService.NodeId, StringComparison.OrdinalIgnoreCase)) return null;
            if ((DateTime.UtcNow - request.TimestampUtc).Duration() > LinkResumeClockSkew) return null;

            var sender = _peerTracker.GetPeer(request.SenderNodeId)?.Identity;
            if (sender?.PublicVerifyKey == null || sender.PublicVerifyKey.Length == 0 ||
                sender.PublicEncryptionKey == null || sender.PublicEncryptionKey.Length == 0) return null;

            // 1. it is really the sender (signature over the encrypted envelope)
            var signed = LinkResumeText(request.SenderNodeId, _identityService.NodeId, request.TimestampUtc.Ticks, request.Envelope);
            if (!P2PModule.Infrastructure.P2PCrypto.TestSignature(signed, sender.PublicVerifyKey, request.Signature))
            {
                _logger.LogWarning("[P2P Transport] Link resume from {NodeId} refused: signature does not verify.", request.SenderNodeId);
                return null;
            }

            // 2. only we can open it (our private encryption key)
            var payload = P2PModule.Infrastructure.P2PCrypto.Decrypt(request.Envelope, _identityService.PrivateKey);
            if (payload.Length != LinkPayloadSize) return null;
            var linkId = new Guid(payload.AsSpan(0, 16));
            var nonceA = payload.AsSpan(16, 32).ToArray();
            long ticks = BitConverter.ToInt64(payload, 48);
            if (ticks != request.TimestampUtc.Ticks) return null;   // envelope belongs to this request

            // 3. we still hold the same link key
            if (!HasLinkKey(request.SenderNodeId, linkId))
            {
                _logger.LogInformation("[P2P Transport] Link resume from {NodeId} refused: we don't hold that link key (lost or expired). A full handshake follows.", request.SenderNodeId);
                _linkKeys.TryRemove(request.SenderNodeId, out _);
                return null;
            }

            var nonceB = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
            var answerPayload = nonceA.Concat(nonceB).ToArray();   // 64 bytes
            var envelope = P2PModule.Infrastructure.P2PCrypto.Encrypt(answerPayload, sender.PublicEncryptionKey);
            var now = DateTime.UtcNow;

            var nextKey = NextLinkKey(linkId, nonceA, nonceB);
            _resumeApprovals[request.SenderNodeId] = new LinkKey(nextKey, now + TimeSpan.FromSeconds(30));

            _logger.LogInformation("[P2P Transport] Link resume from {NodeId} accepted: both sides hold the same link key (checked encrypted + signed).", request.SenderNodeId);
            return new LinkResumeResponse
            {
                SenderNodeId = _identityService.NodeId,
                TimestampUtc = now,
                Envelope = envelope,
                Signature = P2PModule.Infrastructure.P2PCrypto.SignData(
                    LinkResumeText(_identityService.NodeId, request.SenderNodeId, now.Ticks, envelope), _identityService.PrivateSignatureKey)
            };
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[P2P Transport] Link resume from {NodeId} failed.", request?.SenderNodeId);
            return null;
        }
    }

    /// <summary>
    /// Asks the node at targetUrl to confirm, encrypted and signed, that it still holds our link key.
    /// Returns the NEW link key both sides switch to (the handshake id of this reconnect), or null.
    /// </summary>
    private async Task<Guid?> TryResumeLinkAsync(string nodeId, string targetUrl)
    {
        if (!_linkKeys.TryGetValue(nodeId, out var key) || key.ExpiresUtc <= DateTime.UtcNow) return null;
        try
        {
            var peer = _peerTracker.GetPeer(nodeId)?.Identity;
            if (peer?.PublicVerifyKey == null || peer.PublicVerifyKey.Length == 0 ||
                peer.PublicEncryptionKey == null || peer.PublicEncryptionKey.Length == 0) return null;

            var now = DateTime.UtcNow;
            var nonceA = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
            var payload = new byte[LinkPayloadSize];
            key.Id.ToByteArray().CopyTo(payload, 0);
            nonceA.CopyTo(payload, 16);
            BitConverter.GetBytes(now.Ticks).CopyTo(payload, 48);   // 56..63 stay zero

            var envelope = P2PModule.Infrastructure.P2PCrypto.Encrypt(payload, peer.PublicEncryptionKey);
            var request = new LinkResumeRequest
            {
                SenderNodeId = _identityService.NodeId,
                TargetNodeId = nodeId,
                TimestampUtc = now,
                Envelope = envelope,
                Signature = P2PModule.Infrastructure.P2PCrypto.SignData(
                    LinkResumeText(_identityService.NodeId, nodeId, now.Ticks, envelope), _identityService.PrivateSignatureKey)
            };

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var response = await _http.PostAsJsonAsync($"{targetUrl}/p2p/link/resume", request, cts.Token);
            if (!response.IsSuccessStatusCode)
            {
                _linkKeys.TryRemove(nodeId, out _);   // the other side no longer has it: start over
                return null;
            }

            var answer = await response.Content.ReadFromJsonAsync<LinkResumeResponse>(cancellationToken: cts.Token);
            if (answer?.Envelope == null || answer.Signature == null) return null;
            if ((DateTime.UtcNow - answer.TimestampUtc).Duration() > LinkResumeClockSkew) return null;

            var signed = LinkResumeText(nodeId, _identityService.NodeId, answer.TimestampUtc.Ticks, answer.Envelope);
            if (!P2PModule.Infrastructure.P2PCrypto.TestSignature(signed, peer.PublicVerifyKey, answer.Signature))
            {
                _logger.LogWarning("[P2P Transport] Link resume answer from {NodeId} does not verify; running a full handshake.", nodeId);
                return null;
            }

            var answerPayload = P2PModule.Infrastructure.P2PCrypto.Decrypt(answer.Envelope, _identityService.PrivateKey);
            if (answerPayload.Length != LinkPayloadSize || !answerPayload.AsSpan(0, 32).SequenceEqual(nonceA))
            {
                _logger.LogWarning("[P2P Transport] Link resume answer from {NodeId} did not return our nonce; running a full handshake.", nodeId);
                return null;
            }

            return NextLinkKey(key.Id, nonceA, answerPayload.AsSpan(32, 32).ToArray());
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[P2P Transport] Link resume with {NodeId} failed; running a full handshake.", nodeId);
            return null;
        }
    }

    /// <summary>
    /// One connection per pair of nodes. When both dial each other at once, the dial of the node with the
    /// smaller id wins on BOTH sides: the smaller node turns the other's offer away, the larger node accepts
    /// the smaller's offer and drops its own dial. Before this each side kept a different connection.
    /// </summary>
    private bool OurDialWins(string otherNodeId) =>
        string.Compare(_identityService.NodeId, otherNodeId, StringComparison.OrdinalIgnoreCase) < 0;

    public event Action<string, string, P2PPacketEnvelope>? OnRawMessageReceived;
    public event Action<string, string, RTCDataChannel>? OnUnmanagedChannelReceived;

    public PeerConnectionManager(
            ILogger<PeerConnectionManager> logger,
            IOptionsMonitor<P2PSettings> settings,
            HttpClient http,
            INodeIdentityService identityService,
            IPeerTracker peerTracker,
            IRatingService ratingService,
            IChallengeService challengeService)
    {
        _identityService = identityService;
        _challengeService = challengeService;
        _ratingService = ratingService;
        _http = http;
        _peerTracker = peerTracker;
        _settings = settings;
        _logger = logger;


        _http.Timeout = TimeSpan.FromSeconds(_settings.CurrentValue.HttpTimeoutSeconds);
        _handshakeTimeoutTimer = new Timer(CheckHandshakeTimeouts, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));

        // Dynamically resolve time dilation update interval from settings; fall back to 2 minutes
        int syncIntervalMinutes = _settings.CurrentValue.TimeSyncIntervalSeconds > 0
            ? _settings.CurrentValue.TimeSyncIntervalSeconds
            : 120;
        _timeSyncPulseTimer = new Timer((timer) => BroadcastTimeSyncPulses(timer), null, 
            TimeSpan.FromSeconds(syncIntervalMinutes), 
            TimeSpan.FromSeconds(syncIntervalMinutes));

        // Debounced: one settings save fires several change notifications (see OnChangeDebounced).
        _settingsChangeListener = _settings.OnChangeDebounced(_ => UpdateSettings());
    }

    private void UpdateSettings()
    {
        _http.Timeout = TimeSpan.FromSeconds(_settings.CurrentValue.HttpTimeoutSeconds);

        int syncIntervalMinutes = _settings.CurrentValue.TimeSyncIntervalSeconds > 0
            ? _settings.CurrentValue.TimeSyncIntervalSeconds
            : 120;
        _timeSyncPulseTimer.Change(
            TimeSpan.FromSeconds(syncIntervalMinutes),
            TimeSpan.FromSeconds(syncIntervalMinutes));
    }

    #region GeneralMethods
    //public Task<ActivePeerConnection?> GetConnection(string nodeId) => 
    //    Task.FromResult(_connections.TryGetValue(nodeId, out var conn) ? conn : null);
    public ActivePeerConnection? GetConnection(string nodeId) => 
        _connections.TryGetValue(nodeId, out var conn) ? conn : null;
    public IEnumerable<ActivePeerConnection> GetActiveConnections() => 
        _connections.Values;
    #endregion

    /// <summary>
    /// Sends message without return expectations
    /// Requries estabilished connection
    /// </summary>
    /// <param name="nodeId"></param>
    /// <param name="payload"></param>
    /// <param name="channelLabel"></param>
    /// <returns></returns>
    public bool SendReplyRaw(string nodeId, string payload, string channelLabel = "p2p-control-channel")
    {
        // An answer belongs on the connection the request came in on. After two dials crossed, the connection
        // stored for the node can be another (still "open") one that the node no longer reads: answers sent
        // there vanished and the asker's heartbeats failed until the link was rebuilt.
        if (_lastInboundChannel.TryGetValue($"{nodeId}|{channelLabel}", out var inbound) && inbound.readyState == RTCDataChannelState.open)
        {
            inbound.send(payload);
            return true;
        }
        return SendMessageRaw(nodeId, payload, channelLabel);
    }

    public bool SendMessageRaw(string nodeId, string payload, string channelLabel = "p2p-control-channel")
    {
        // The current connection's channel first. (Preferring the channel the node last talked on was tried and
        // made things worse: after a reconnect that pointed at the OLD connection's channel, which still said
        // "open", so control messages went into it while the new connection sat idle - the time-sync channel,
        // sent directly, kept working on the same link.) The inbound channel is only a fallback below.
        if (_connections.TryGetValue(nodeId, out var conn))
        {
            if (conn.DataChannels.TryGetValue(channelLabel, out var dc) && dc.readyState == RTCDataChannelState.open)
            {
                dc.send(payload);
                //conn.LastActivity = DateTime.UtcNow;
                return true;
            }
        }

        // The stored connection's channel isn't open, but the node is still talking to us over another one
        if (_lastInboundChannel.TryGetValue($"{nodeId}|{channelLabel}", out var inbound) && inbound.readyState == RTCDataChannelState.open)
        {
            inbound.send(payload);
            return true;
        }
        return false;
    }

    private async Task<CreateChannelResult> CreateDataChannelInternal(string nodeId, string channelLabel, bool isUnmanaged)
    {
        if (string.IsNullOrEmpty(nodeId) || string.IsNullOrEmpty(channelLabel))
            return new CreateChannelResult
            {
                Error = $"Param empty Id:[{nodeId}] ch:[{channelLabel}]"
            };

        if (_connections.TryGetValue(nodeId, out var conn))
        {
            if (conn.PeerConnection != null && conn.PeerConnection.connectionState == RTCPeerConnectionState.connected)
            {
                if (conn.IsHandshaked)
                {
                    string cleanLabel = isUnmanaged && channelLabel.StartsWith("unmanaged:", StringComparison.OrdinalIgnoreCase)
                        ? channelLabel.Substring("unmanaged:".Length)
                        : channelLabel;

                    // Reuse existing open channel if already present under this clean label
                    if (conn.DataChannels.TryGetValue(cleanLabel, out var existingChannel) &&
                        existingChannel != null &&
                        existingChannel.readyState == RTCDataChannelState.open)
                    {
                        _logger.LogInformation("[PeerConnectionManager] Reusing open channel '{Label}' to {NodeId}.", cleanLabel, nodeId);
                        return new CreateChannelResult
                        {
                            Channel = existingChannel
                        };
                    }

                    string wireLabel = isUnmanaged
                        ? (channelLabel.StartsWith("unmanaged:", StringComparison.OrdinalIgnoreCase) ? channelLabel : $"unmanaged:{channelLabel}")
                        : channelLabel;

                    var dc = await conn.PeerConnection.createDataChannel(wireLabel);
                    if (dc == null)
                    {
                        return new CreateChannelResult { Error = $"createDataChannel('{wireLabel}') returned no channel" };
                    }
                    RegisterAndBindDataChannel(nodeId, dc, isUnmanaged: isUnmanaged);
                    if (isUnmanaged)
                    {
                        _logger.LogInformation("[PeerConnectionManager] Created channel '{Label}' to {NodeId} ({State}).", wireLabel, nodeId, dc.readyState);
                    }

                    return new CreateChannelResult
                    {
                        Channel = dc
                    };
                }
                else
                {
                    return new CreateChannelResult { Error = "No handshake" };
                }
            }
            else
            {
                return new CreateChannelResult { Error = "No connection" };
            }
        }
        else
        {
            return new CreateChannelResult { Error = "No connection" };
        }
    }


    /// <summary>
    /// This channel receives messages and is routed through OnMessageReceived
    /// Mesages over this channel are generalyl P2PPacketEnvelope -s
    /// Messages have a topic that router subscribes and routes to
    /// So those are processed by router and then routed to subscribed topics
    /// </summary>
    /// <param name="nodeId">node id of channel</param>
    /// <param name="channelLabel">name of channel</param>
    /// <returns></returns>
    public Task<CreateChannelResult> CreateManagedDataChannelAsync(string nodeId, string channelLabel)
        => CreateDataChannelInternal(nodeId, channelLabel, false);

    /// <summary>
    /// the channel that you can do whatever you want with it 
    /// subscribe to onmessage event of channel and use it at your leisure
    /// It does not participate or feed into the router message loop
    /// closing the channel automatically removes it from the conenction
    /// so in case it was closed unintentionally - it has to be reopened
    /// </summary>
    /// <param name="nodeId">node id of channel</param>
    /// <param name="channelLabel">name of channel</param>
    /// <returns></returns>
    public Task<CreateChannelResult> CreateUnmanagedDataChannelAsync(string nodeId, string channelLabel)
        => CreateDataChannelInternal(nodeId, channelLabel, true);

    private async Task BumpPeerConnectivityAsync(string nodeId)
    {
        var peerNode = _peerTracker.GetPeer(nodeId);
        if (peerNode != null)
        {
            peerNode.LastSeen = DateTime.UtcNow;
            peerNode.IsVerified = true;

            // Sync connection clock offset to model if connection exists
            if (_connections.TryGetValue(nodeId, out var conn) && conn.ClockOffsetMs != 0)
            {
                peerNode.ClockOffsetMs = conn.ClockOffsetMs;
            }

            await _peerTracker.UpdatePeerAsync(peerNode);
        }
    }

    #region ConnectionMethods

    #region ConnectionMethods.Maintanence

    private bool SendTimeSyncPacketAsync(string nodeId)
    {
        try
        {
            var syncPacket = new TimeSyncPacket { 
                SenderSendUtc = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() 
            };
            string innerPayload = JsonSerializer.Serialize(syncPacket);

            var envelope = new P2PPacketEnvelope
            {
                SenderNodeId = _identityService.NodeId,
                TargetNodeId = nodeId,
                CommandType = "TimeSync",
                SerializedPayload = innerPayload,
                Ttl = 1,
            };

            return SendMessageRaw(nodeId, JsonSerializer.Serialize(envelope), TimesyncChannel);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[P2P Sync] Failed to send TimeSync packet to peer: {NodeId}", nodeId);
            return false;
        }
    }

    private async void BroadcastTimeSyncPulses(object? state)
    {
        foreach (var conn in _connections.Values)
        {
            if (conn.PeerConnection?.connectionState == RTCPeerConnectionState.connected && conn.IsHandshaked)
            {
                if (!conn.DataChannels.ContainsKey(TimesyncChannel))
                {
                    var createChannel = 
                        await CreateUnmanagedDataChannelAsync(conn.NodeId, TimesyncChannel);
                    if (createChannel.Channel == null) continue;
                    else
                    {
                        createChannel.Channel.onmessage += (_, _, data) =>
                            HandleTimeSyncPacket(conn.NodeId, data);
                    }
                }

                _ = SendTimeSyncPacketAsync(conn.NodeId);
            }
        }
    }

    private async void HandleTimeSyncPacket(string nodeId, byte[] data)
    {
        string rawJsonText = Encoding.UTF8.GetString(data);
        var envelope = JsonSerializer.Deserialize<P2PPacketEnvelope>(rawJsonText);
        if (envelope == null) return;
        if (envelope.CommandType == "TimeSync")
        {
            var sync = JsonSerializer.Deserialize<TimeSyncPacket>(envelope.SerializedPayload);
            if (sync != null && _connections.TryGetValue(nodeId, out var conn))
            {
                long t4 = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); // Arrival Time
                long t1 = sync.SenderSendUtc;
                long offsetVal = t4 - t1; // direct calculation (receiverReceivedUtc - senderSendUtc)

                conn.UpdateOffset(offsetVal);
                _logger.LogInformation("[P2P Sync] TimeSync offset received from {NodeId}: {Offset}ms. Consolidated network offset: {Consolidated}ms", nodeId, offsetVal, conn.ClockOffsetMs);

                var peerNode = _peerTracker.GetPeer(nodeId);
                if (peerNode != null && peerNode.ClockOffsetMs != conn.ClockOffsetMs)
                {
                    peerNode.ClockOffsetMs = conn.ClockOffsetMs;
                    await _peerTracker.UpdatePeerAsync(peerNode);
                }
            }
            return;
        }
    }

    private void CheckHandshakeTimeouts(object? state)
    {
        var now = DateTime.UtcNow;
        int maxInactivity = 
            _settings.CurrentValue.DefaultHandshakeTimeoutSeconds > 0 ? 
            _settings.CurrentValue.DefaultHandshakeTimeoutSeconds : 
            10;
        var timeoutLimit = TimeSpan.FromSeconds(maxInactivity);

        foreach (var kvp in _connections.Values.Where(c => !c.IsHandshaked))
        {
            //string nodeId = kvp.Key;
            var conn = kvp;
            if (!conn.IsHandshaked)
            {
                if (now - conn.ConnectedAt > timeoutLimit) 
                    _ = KillConnectionAsync(kvp.NodeId, "Unhandshaked connection authentication timeout");
            }
        }
    }
    #endregion

    #region ConnectionMethods.Utility

    private void HandleRemoteDataChannel(string targetNodeId, RTCDataChannel remoteChannel)
    {
        if (remoteChannel.label == TimesyncChannel)
        {
            RegisterAndBindTimeDataChannel(targetNodeId, remoteChannel);
        }
        else
        if (remoteChannel.label == ControlChannel)
        {
            RegisterAndBindHandshakeControlDataChannel(targetNodeId, remoteChannel);
        }
        else if (remoteChannel.label.StartsWith("unmanaged:", StringComparison.OrdinalIgnoreCase))
        {
            HandleIncomingUnmanagedDataChannel(targetNodeId, remoteChannel);
        }
        else
        {
            RegisterAndBindDataChannel(targetNodeId, remoteChannel);
        }
    }

    private void HandleIncomingUnmanagedDataChannel(string senderNodeId, RTCDataChannel remoteChannel)
    {
        if (!_connections.TryGetValue(senderNodeId, out var conn))
        {
            _logger.LogWarning("[PeerConnectionManager] Received unmanaged channel '{Label}' from unknown sender {SenderNodeId}.", remoteChannel.label, senderNodeId);
            return;
        }

        string cleanLabel = remoteChannel.label.StartsWith("unmanaged:", StringComparison.OrdinalIgnoreCase)
            ? remoteChannel.label.Substring("unmanaged:".Length)
            : remoteChannel.label;

        conn.DataChannels[cleanLabel] = remoteChannel;

        remoteChannel.onclose += () =>
        {
            RemoveChannelIfCurrent(conn, cleanLabel, remoteChannel);
            _logger.LogInformation("[PeerConnectionManager] Unmanaged data channel '{Label}' closed for peer {SenderNodeId}.", cleanLabel, senderNodeId);
        };

        remoteChannel.onerror += (err) =>
        {
            _logger.LogError("[PeerConnectionManager] Error on unmanaged data channel '{Label}' for peer {SenderNodeId}: {Error}", cleanLabel, senderNodeId, err);
        };

        _logger.LogInformation("[PeerConnectionManager] Dispatched inbound unmanaged data channel '{Label}' from {SenderNodeId}.", cleanLabel, senderNodeId);

        OnUnmanagedChannelReceived?.Invoke(senderNodeId, cleanLabel, remoteChannel);
    }

    private void UpdateConnectionActivity(string nodeId)
    {
        if (_connections.TryGetValue(nodeId, out var activeConn))
        {
            if (activeConn.IsHandshaked)
            {
                activeConn.LastActivity = DateTime.UtcNow;
                _peerTracker.UpdatePeerActivityAsync(nodeId);
            }
        }
    }


    /// <summary>
    /// Forgets a closed channel - but only if the connection still maps the label to THAT channel. Channels are
    /// registered by label, and a re-dial or a re-opened file channel registers a new channel under the same label
    /// before the old one's close event arrives; removing by label alone then dropped the new, working channel
    /// (e.g. "p2p-control-channel": every request and heartbeat over a healthy connection timed out after that).
    /// </summary>
    private static void RemoveChannelIfCurrent(ActivePeerConnection conn, string label, RTCDataChannel closed)
    {
        ((ICollection<KeyValuePair<string, RTCDataChannel>>)conn.DataChannels).Remove(new KeyValuePair<string, RTCDataChannel>(label, closed));
    }

    private void RegisterAndBindDataChannel(string nodeId, RTCDataChannel dc, bool isUnmanaged = false)
    {
        if (_connections.TryGetValue(nodeId, out var conn))
        {
            string cleanLabel = isUnmanaged && dc.label.StartsWith("unmanaged:", StringComparison.OrdinalIgnoreCase)
                ? dc.label.Substring("unmanaged:".Length)
                : dc.label;

            conn.DataChannels[cleanLabel] = dc;

            dc.onclose += () =>
            {
                RemoveChannelIfCurrent(conn, cleanLabel, dc);
            };

            dc.onerror += (err) =>
            {
                _logger.LogError("[WebRTC Channel Error] Node {NodeId} - Channel {Channel}: {Error}", nodeId, cleanLabel, err);
            };

            if (!isUnmanaged)
            {
                dc.onmessage += (upgradedChannel, upgradedProtocol, productionData) =>
                {
                    try
                    {
                        string rawJsonText = Encoding.UTF8.GetString(productionData);
                        var envelope = JsonSerializer.Deserialize<P2PPacketEnvelope>(rawJsonText);

                        if (envelope != null)
                        {
                            if (_connections.TryGetValue(nodeId, out var connRef) && connRef.ClockOffsetMs != 0)
                            {
                                envelope.CreatedUtc = envelope.CreatedUtc.AddMilliseconds(-connRef.ClockOffsetMs);
                            }
                            _lastInboundChannel[$"{nodeId}|{cleanLabel}"] = upgradedChannel;
                            _lastInboundUtc[nodeId] = DateTime.UtcNow;
                            if (!_offline.IsEmpty) MarkOnline(nodeId);
                            OnRawMessageReceived?.Invoke(nodeId, cleanLabel, envelope);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "[WebRTC Router Ingest Error] Raw message deserialize failed on {Label} from {NodeId}", cleanLabel, nodeId);
                    }
                };
            }
        }
    }

    private void InitiateWebRtcHandshakeAsync(string nodeId)
    {
        try
        {
            var challengeReq = _challengeService.CreateHandshakeRequest(nodeId);
            var envelope = new P2PPacketEnvelope
            {
                SenderNodeId = _identityService.NodeId,
                TargetNodeId = nodeId,
                CommandType = _challengeService.HandshakeStartCommand,
                SerializedPayload = JsonSerializer.Serialize(challengeReq),
            };
            SendMessageRaw(nodeId, JsonSerializer.Serialize(envelope));
        }
        catch { }
    }

    private void RegisterAndBindHandshakeControlDataChannel(string nodeId, RTCDataChannel dc)
    {
        if (dc == null) return;
        string label = dc.label ?? "unknown-channel";

        dc.onclose += () =>
        {
            if (_connections.TryGetValue(nodeId, out var activeConn))
                RemoveChannelIfCurrent(activeConn, label, dc);
        };

        if (_connections.TryGetValue(nodeId, out var conn))
        {
            if (conn.IsHandshaked &&
                conn.DataChannels.TryGetValue(label, out var previous) &&
                previous != null && !ReferenceEquals(previous, dc))
            {
                // The other node recreated the control channel (its sends weren't getting through): use the new one
                // from our side too, so both ends talk on the same channel again.
                _logger.LogInformation("[P2P Transport] {NodeId} opened a new control channel; switching to it and closing the old one.", nodeId);
                ForgetInbound(nodeId);
                var old = previous;
                dc.onopen += () => { try { old.close(); } catch { } };
            }

            conn.DataChannels[label] = dc;
            if(!conn.IsHandshaked)
            {
                dc.onopen += () => InitiateWebRtcHandshakeAsync(nodeId);
                // Define the initial handshake gatekeeper action delegate block
                OnDataChannelMessageDelegate onMessageAction = null!;
                onMessageAction = (channel, protocol, data) =>
                {
                    try
                    {
                        string rawJsonText = Encoding.UTF8.GetString(data);

                        var lookaheadHeader = JsonSerializer.Deserialize<P2PPacketEnvelope>(rawJsonText);
                        if (lookaheadHeader != null)
                        {
                            // EXCLUSION SET: Gated handshake control payloads bypass standard routing requirements
                            if (!_challengeService.IsHandshakeCommand(lookaheadHeader.CommandType)) return;

                            // Offload asynchronous context running off event synchronization thread execution limits
                            Task.Run(async () =>
                            {
                                try
                                {
                                    //this also sets conenction.ishandshaked for challenge session
                                    bool handshakePassed = await _challengeService.HandleHandshakeMessage(nodeId, dc, lookaheadHeader);

                                    if (handshakePassed)
                                    {
                                        if (_connections.TryGetValue(nodeId, out var conn))
                                        {
                                            conn.IsHandshaked = true;
                                            conn.HandshakedAtUtc = DateTime.UtcNow;
                                            conn.ConnectedAt = DateTime.UtcNow;
                                            conn.LastActivity = DateTime.UtcNow;
                                            if (!conn.ConnectionTypes.Contains("outbound"))
                                            {
                                                conn.ConnectionTypes.Add("inbound");
                                            }
                                        }

                                        // ATOMIC UPGRADE: Safely unbind gatekeeper code and promote data channel to permanent messaging route
                                        dc.onmessage -= onMessageAction;
                                        dc.onmessage += (upgradedChannel, upgradedProtocol, productionData) =>
                                        {
                                            try
                                            {
                                                string rawJsonText = Encoding.UTF8.GetString(productionData);
                                                var envelope = JsonSerializer.Deserialize<P2PPacketEnvelope>(rawJsonText);

                                                if (envelope != null)
                                                {
                                                    if (_connections.TryGetValue(nodeId, out var conn))
                                                    {
                                                        envelope.CreatedUtc = envelope.CreatedUtc.AddMilliseconds(conn.ClockOffsetMs);
                                                    }
                                                    _lastInboundChannel[$"{nodeId}|{label}"] = upgradedChannel;
                                                    _lastInboundUtc[nodeId] = DateTime.UtcNow;
                                                    if (!_offline.IsEmpty) MarkOnline(nodeId);
                                                    OnRawMessageReceived?.Invoke(nodeId, label, envelope);
                                                }
                                            }
                                            catch { }
                                        };
                                        dc.onmessage += (channel, protocol, data) => UpdateConnectionActivity(nodeId);
                                        UpdateConnectionActivity(nodeId);
                                    }
                                }
                                catch { }
                            });

                            if (_connections.TryGetValue(nodeId, out var conn))
                            {
                                conn.UpdateOffset((lookaheadHeader.CreatedUtc - DateTime.UtcNow).Milliseconds);
                            }

                        }
                    }
                    catch { }
                };

                // Bind gatekeeper configuration initialization sample properties
                dc.onmessage += onMessageAction;
            }
            else
            {
                dc.onopen += () => {

                };

                dc.onmessage += (channel, protocol, data) => UpdateConnectionActivity(nodeId);

                dc.onmessage += (channel, protocol, data) =>
                {
                    try
                    {
                        string rawJsonText = Encoding.UTF8.GetString(data);
                        var envelope = JsonSerializer.Deserialize<P2PPacketEnvelope>(rawJsonText);

                        if (envelope != null)
                        {
                            if (_connections.TryGetValue(nodeId, out var conn))
                            {
                                envelope.CreatedUtc = envelope.CreatedUtc.AddMilliseconds(conn.ClockOffsetMs);
                            }
                            // Same bookkeeping as the other receive paths: without it an answer to a request that
                            // came in on this (re-created) channel had nowhere to go ("channel not open").
                            _lastInboundChannel[$"{nodeId}|{label}"] = channel;
                            _lastInboundUtc[nodeId] = DateTime.UtcNow;
                            if (!_offline.IsEmpty) MarkOnline(nodeId);
                            OnRawMessageReceived?.Invoke(nodeId, label, envelope);
                        }
                    }
                    catch { }
                };
            }
        }
    }

    private void RegisterAndBindTimeDataChannel(string nodeId, RTCDataChannel dc)
    {
        if (dc == null) return;
        string label = dc.label ?? "unknown-channel";

        if (_connections.TryGetValue(nodeId, out var conn))
        {
            conn.DataChannels[label] = dc;
        }

        dc.onopen += () => { };

        dc.onclose += () =>
        {
            if (_connections.TryGetValue(nodeId, out var activeConn)) RemoveChannelIfCurrent(activeConn, label, dc);
        };

        dc.onmessage += (channel, protocol, data) => UpdateConnectionActivity(nodeId);
        dc.onmessage += (channel, protocol, data) => HandleTimeSyncPacket(nodeId, data);
    }

    #endregion

    /// <summary>
    /// Probes a specific endpoint for cryptographic PONG confirmation.
    /// </summary>
    private static System.Net.Sockets.SocketError? FindSocketError(Exception ex)
    {
        for (var e = ex; e != null; e = e.InnerException)
            if (e is System.Net.Sockets.SocketException se) return se.SocketErrorCode;
        return null;
    }

    public async Task<string> GetEndpointActiveAsync(string endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint)) return string.Empty;
        _endpointRefused.TryRemove(endpoint, out _);

        try
        {
            string fullPingUrl = $"{endpoint}/p2p/ping";

            // Execute out-of-band check over the shared _http configuration client
            using var response = await _http.GetHttpAsync(fullPingUrl, completionOption: HttpCompletionOption.ResponseHeadersRead);

            if (response != null && response.IsSuccessStatusCode)
            {
                string content = await response.Content.ReadAsStringAsync();
                if(content.Trim() == "PONG")
                {
                    return response.RequestMessage!.RequestUri!.GetLeftPart(UriPartial.Authority);
                }
            }

            return string.Empty;
        }
        catch (HttpRequestException ex) when (FindSocketError(ex) == System.Net.Sockets.SocketError.ConnectionRefused)
        {
            // Nothing listens there: the node's process is not running (not a NAT/firewall timeout)
            _endpointRefused[endpoint] = true;
            return string.Empty;
        }
        catch (HttpRequestException ex)
        {
            // Suppress logs for protocol fallback validation (e.g., HTTPS failed, falling back to HTTP)
            _logger.LogTrace("[P2P Transport] Silent fallback check trace for {Url}: {Msg}", endpoint, ex.Message);
            return string.Empty;
        }
        catch (TaskCanceledException)
        {
            // Caught cleanly if connection times out (e.g., client 3s window expires)
            return string.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogDebug("[P2P Transport] Unexpected ping failure on candidate {Url}: {Msg}", endpoint, ex.Message);
            return string.Empty;
        }
    }

    //general peer connection method
    /// <summary>
    /// Several services dial on their own (consensus visits, mesh management, heartbeat re-dial). Two dials to
    /// the same node at once each built a new RTCPeerConnection and the second closed the first one mid-setup,
    /// leaving the two sides on different connections: requests went out on one, answers came back on a closed
    /// one, and every heartbeat timed out until the link was rebuilt. So dials to one node are serialized; the
    /// second caller then finds the connection the first one made and reuses it.
    /// </summary>
    public async Task<ActivePeerConnection?> ConnectPeerAsync(string nodeId, string[] signalingEndpoints, string connectionType)
    {
        if (string.IsNullOrEmpty(nodeId)) return null;

        var dialLock = _dialLocks.GetOrAdd(nodeId, _ => new SemaphoreSlim(1, 1));
        if (!await dialLock.WaitAsync(TimeSpan.FromSeconds(30)))
        {
            _logger.LogWarning("[P2P Transport] Another dial to {NodeId} is still running after 30 s; skipping this one.", nodeId);
            return _connections.TryGetValue(nodeId, out var running) ? running : null;
        }

        try
        {
            return await ConnectPeerCoreAsync(nodeId, signalingEndpoints, connectionType);
        }
        finally
        {
            dialLock.Release();
        }
    }

    private async Task<ActivePeerConnection?> ConnectPeerCoreAsync(string nodeId, string[] signalingEndpoints, string connectionType)
    {
        if (string.Equals(nodeId, _identityService.NodeId, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogDebug("[P2P Transport] Not connecting to {NodeId}: that is this node.", nodeId);
            return null;
        }

        _logger.LogInformation("[P2P Transport] Establishing WebRTC session with {NodeId}.", nodeId);

        // A connection to this node is still coming up (typically: it just dialled us and the answer is out).
        // Dialling now would close that half-built connection and start a second one - the other side would
        // keep the first, and the two ends end up on different connections. Let it finish instead.
        if (_connections.TryGetValue(nodeId, out var pending) &&
            pending.PeerConnection != null &&
            (pending.PeerConnection.connectionState == RTCPeerConnectionState.@new ||
             pending.PeerConnection.connectionState == RTCPeerConnectionState.connecting) &&
            !_dialsInProgress.ContainsKey(nodeId) &&
            DateTime.UtcNow - pending.ConnectedAt < TimeSpan.FromSeconds(15))
        {
            var until = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (pending.PeerConnection.connectionState != RTCPeerConnectionState.connected && DateTime.UtcNow < until)
                await Task.Delay(200);
            if (pending.PeerConnection.connectionState == RTCPeerConnectionState.connected)
            {
                _logger.LogInformation("[P2P Transport] {NodeId} was already connecting to us; using that connection instead of dialling.", nodeId);
                if (!pending.ConnectionTypes.Contains(connectionType)) pending.ConnectionTypes.Add(connectionType);
                return pending;
            }
        }

        if (_connections.TryGetValue(nodeId, out var existingConn) &&
            existingConn.PeerConnection.connectionState == RTCPeerConnectionState.connected &&
            !(connectionType == "temporary" && IsSilent(nodeId)))
        {
            if (!existingConn.ConnectionTypes.Contains(connectionType))
                existingConn.ConnectionTypes.Add(connectionType);
                existingConn.LastActivity = DateTime.UtcNow;

                var node = _peerTracker.GetPeer(nodeId);
                if (node != null)
                {
                    node.LastSeen = DateTime.UtcNow;
                    await _peerTracker.UpdatePeerAsync(node);
                }

            return existingConn;
        }

        if (!_ratingService.IsConnectivityValid(nodeId) && connectionType != "outbound")
        {
            return null;
        }

        // A peer record whose endpoints are all ours is this node under an old/other id: drop it instead of dialing ourselves
        var ownEndpointList = _identityService.GetEndpoints() ?? new List<string>();
        if (signalingEndpoints.Length > 0 &&
            signalingEndpoints.All(e => ownEndpointList.Any(own => EndpointText.SameAuthority(own, e))))
        {
            _logger.LogWarning("[P2P Transport] Peer {NodeId} only lists this node's own endpoints - it is a stale record of this node. Removing it.", nodeId);
            await KillConnectionAsync(nodeId, "self record");
            try { _peerTracker.RemovePeer(nodeId); } catch (Exception ex) { _logger.LogDebug(ex, "[P2P Transport] Could not remove stale peer {NodeId}.", nodeId); }
            return null;
        }

        if (IsOffline(nodeId))
        {
            _logger.LogDebug("[P2P Transport] Not dialling {NodeId}: it is offline.", nodeId);
            return null;
        }

        // HTTP-First Flow
        //ping for active url
        List<string> activeEndpoints = new();
        var ownEndpoints = _identityService.GetEndpoints() ?? new List<string>();
        foreach (var endpoint in signalingEndpoints)
        {
            // Our own tunnel/LAN address listed for another node would only connect us to ourselves
            if (ownEndpoints.Any(own => EndpointText.SameAuthority(own, endpoint)))
            {
                _logger.LogDebug("[P2P Transport] Skipping {Endpoint} for {NodeId}: it is this node's own endpoint.", endpoint, nodeId);
                continue;
            }

            var target = string.Empty;
            if (!string.IsNullOrEmpty(target = await GetEndpointActiveAsync(endpoint)))
            {
                activeEndpoints.Add(target);
            }
        }

        var probed = signalingEndpoints.Where(e => !ownEndpoints.Any(own => EndpointText.SameAuthority(own, e))).ToList();
        if (activeEndpoints.Count == 0 && probed.Count > 0 && probed.All(e => _endpointRefused.ContainsKey(e)))
        {
            // Every address refuses the connection: the node is down. No relay attempt - there is nothing to reach.
            MarkOffline(nodeId, "its endpoints refuse connections");
            return null;
        }

        if (activeEndpoints.Any(e => !string.IsNullOrEmpty(e)))
        {
            //http cnnection available
            Guid? handshakeId = null;
            var target = string.Empty;

            // Reconnect on the link key both sides kept from the last connection, if both still hold it
            if (_linkKeys.TryGetValue(nodeId, out var linkKey) && linkKey.ExpiresUtc > DateTime.UtcNow)
            {
                foreach (var targetUrl in activeEndpoints)
                {
                    var nextKey = await TryResumeLinkAsync(nodeId, targetUrl);
                    if (nextKey != null)
                    {
                        handshakeId = nextKey;
                        target = targetUrl;
                        _logger.LogInformation("[P2P Transport] Reconnecting to {NodeId} on the kept link key (no full handshake).", nodeId);
                        break;
                    }
                }
            }

            foreach (var targetUrl in handshakeId == null ? activeEndpoints : new List<string>())
            {
                handshakeId = await TryHttpChallengeHandshakeAsync(nodeId, targetUrl);
                if (handshakeId != null && handshakeId != Guid.Empty)
                {
                    target = targetUrl;
                    break;
                }
            }

            if (handshakeId != null &&
                _connections.TryGetValue(nodeId, out var arrived) &&
                arrived.PeerConnection?.connectionState == RTCPeerConnectionState.connected &&
                !IsSilent(nodeId))
            {
                // The other node dialled us while we were handshaking: use that connection, don't make a second one
                if (!arrived.ConnectionTypes.Contains(connectionType)) arrived.ConnectionTypes.Add(connectionType);
                return arrived;
            }

            if(handshakeId != null)
            {
                var configuration = new RTCConfiguration { iceServers = _settings.CurrentValue.StunServers?.Select(stun => new RTCIceServer { urls = stun }).ToList() ?? new List<RTCIceServer>() };
                var pc = new RTCPeerConnection(configuration);
                _dialsInProgress[nodeId] = 0;

                // The previous connection stays open until the other side has answered the new one. Closing it up
                // front broke a working link whenever the other side turned the new dial away: it kept the old
                // connection and went on sending on it, while this side had already dropped it and every answer
                // went to a closed channel ("could not send the answer to Ping ... channel not open").
                RTCPeerConnection? previousPc = null;
                var previousChannels = new Dictionary<string, RTCDataChannel>();
                DateTime previousConnectedAt = default;

                if (!_connections.TryGetValue(nodeId, out var conn))
                {
                    conn = new ActivePeerConnection
                    {
                        NodeId = nodeId,
                        PeerConnection = pc,
                        ConnectedAt = DateTime.UtcNow,
                        LastActivity = DateTime.UtcNow,
                        IsHandshaked = true,
                        HandshakedAtUtc = DateTime.UtcNow
                    };
                    _connections[nodeId] = conn;
                }
                else
                {
                    previousPc = conn.PeerConnection;
                    foreach (var kv in conn.DataChannels) previousChannels[kv.Key] = kv.Value;
                    previousConnectedAt = conn.ConnectedAt;
                    conn.PeerConnection = pc;
                    conn.ConnectedAt = DateTime.UtcNow;
                    conn.LastActivity = DateTime.UtcNow;
                    conn.IsHandshaked = true;
                    conn.HandshakedAtUtc = DateTime.UtcNow;
                }
                conn.ConnectionTypes.Add(connectionType);

                pc.ondatachannel += (remoteChannel) => HandleRemoteDataChannel(nodeId, remoteChannel);

                var dc = await pc.createDataChannel(ControlChannel);
                RegisterAndBindDataChannel(nodeId, dc);
                var tdc = await pc.createDataChannel(TimesyncChannel);
                RegisterAndBindTimeDataChannel(nodeId, tdc);

                var offer = pc.createOffer();
                await pc.setLocalDescription(offer);

                try
                {
                    var response = await _http.PostAsJsonAsync($"{target}/p2p/webrtc/signal",
                        new WebRTCSignalPayload
                        {
                            SenderNodeId = _identityService.NodeId,
                            Sdp = offer.sdp,
                            Type = RTCSdpType.offer.ToString(),
                            HandShakeId = handshakeId.Value
                        });

                    if (response.IsSuccessStatusCode)
                    {
                        var answer = await response.Content.ReadFromJsonAsync<WebRTCSignalPayload>();
                        if (answer != null && !string.IsNullOrEmpty(answer.Sdp))
                        {
                            _dialsInProgress.TryRemove(nodeId, out _);
                            if (!ReferenceEquals(conn.PeerConnection, pc))
                            {
                                // The other node's dial won the tie-break while ours was in flight: keep theirs
                                try { pc.close(); } catch { }
                                if (previousPc != null && !ReferenceEquals(previousPc, conn.PeerConnection)) { try { previousPc.close(); } catch { } }
                                return conn;
                            }

                            // Answered: from here on the new connection is the one; the old one goes
                            ForgetInbound(nodeId);
                            if (previousPc != null && !ReferenceEquals(previousPc, pc)) { try { previousPc.close(); } catch { } }
                            pc.setRemoteDescription(new RTCSessionDescriptionInit { sdp = answer.Sdp, type = RTCSdpType.answer });
                            RememberLinkKey(nodeId, handshakeId.Value);
                            MarkOnline(nodeId);

                            await BumpPeerConnectivityAsync(nodeId);
                            //todo: throtttle once per day
                            await _ratingService.ApplyLocalReputationUpdateAsync(nodeId, baseAmount: 1);
                            return conn;
                        }
                    }
                }
                catch 
                { 
                
                }

                _dialsInProgress.TryRemove(nodeId, out _);
                pc.close();

                if (previousPc != null && ReferenceEquals(conn.PeerConnection, pc))
                {
                    var prevState = previousPc.connectionState;
                    if (prevState == RTCPeerConnectionState.connected || prevState == RTCPeerConnectionState.connecting || prevState == RTCPeerConnectionState.@new)
                    {
                        // The other side turned the new dial away and still uses the old connection: go back to it
                        conn.PeerConnection = previousPc;
                        conn.ConnectedAt = previousConnectedAt;
                        foreach (var kv in previousChannels) conn.DataChannels[kv.Key] = kv.Value;
                        _logger.LogInformation("[P2P Transport] {NodeId} did not take the new connection; keeping the one we already had with it.", nodeId);
                        return prevState == RTCPeerConnectionState.connected ? conn : null;
                    }
                    try { previousPc.close(); } catch { }
                }
            }

            return null;
        }
        else
        {
            // WebRTC-First Flow (Wait for data channel open to fire inline handshakes)
            string gatewayId = string.Empty;
            var peerNode = _peerTracker.GetPeer(nodeId);

            if (peerNode != null && peerNode.LastSeenByNodeId.Count > 0)
            {
                var reportedGatewayConn = GetActiveConnectionForRelay(peerNode.LastSeenByNodeId);
                if (reportedGatewayConn != null) gatewayId = reportedGatewayConn.NodeId;

                if (!string.IsNullOrEmpty(gatewayId))
                    return await InitiateRelayedConnectionAsync(nodeId, new string[] { gatewayId }, connectionType);
            }

            //try anyway
            var fallbackGateways = _connections.Keys.Where(id => _connections[id].ConnectionTypes.Contains("outbound"));
            var relayed = await InitiateRelayedConnectionAsync(nodeId, fallbackGateways.ToArray(), connectionType);
            if (relayed == null) MarkOffline(nodeId, "no endpoint answers and no relay reaches it");
            return relayed;
        }
    }

    //private ActivePeerConnection? GetActiveConnectionForRelay(IEnumerable<string> nodeList)
    //{
    //    foreach(var nodeId in nodeList)
    //    {
    //        if(_connections.TryGetValue(nodeId, out var conn) && conn.IsHandshaked 
    //            && conn.PeerConnection.connectionState == RTCPeerConnectionState.connected)
    //        {
    //            return conn;
    //        }
    //    }

    //    return null;
    //}

    private ActivePeerConnection? GetActiveConnectionForRelay(IEnumerable<string> nodeList)
    {
        var visited = new HashSet<string>();
        return FindActiveRelayDfs(nodeList, visited);
    }

    private ActivePeerConnection? FindActiveRelayDfs(IEnumerable<string> nodeList, HashSet<string> visited)
    {
        foreach (var nodeId in nodeList)
        {
            if (string.IsNullOrEmpty(nodeId) || !visited.Add(nodeId))
            {
                // have been in here
                continue;
            }

            // found direct webrtc connection
            if (_connections.TryGetValue(nodeId, out var conn) && conn.IsHandshaked
                && conn.PeerConnection.connectionState == RTCPeerConnectionState.connected)
            {
                _logger.LogInformation("[P2P Transport] Found active relay path bridge targeting node {NodeId}", nodeId);
                return conn;
            }

            // go recursive
            var peerNode = _peerTracker.GetPeer(nodeId);
            if (peerNode != null && peerNode.LastSeenByNodeId != null && peerNode.LastSeenByNodeId.Count > 0)
            {
                // go DFS deeper 
                var deepConn = FindActiveRelayDfs(peerNode.LastSeenByNodeId, visited);
                if (deepConn != null)
                {
                    return deepConn;
                }
            }
        }

        return null;
    }

    private async Task<ActivePeerConnection?> InitiateRelayedConnectionAsync(string targetNodeId, string[] relayNodeIds, string connectionType)
    {
        //todo: upgrade!
        if (relayNodeIds == null || relayNodeIds.Length == 0) return null;

        var configuration = new RTCConfiguration
        {
            iceServers = _settings.CurrentValue.StunServers?.Select(stun => new RTCIceServer { urls = stun }).ToList() ?? new List<RTCIceServer>()
        };
        var pc = new RTCPeerConnection(configuration);
        ForgetInbound(targetNodeId);   // a new connection: channels of the old one must not be used any more

        if (!_connections.TryGetValue(targetNodeId, out var conn))
        {
            conn = new ActivePeerConnection
            {
                NodeId = targetNodeId,
                PeerConnection = pc,
                IsHandshaked = false
            };
        }
        else
        {
            conn.PeerConnection = pc;
            conn.ConnectionTypes.Add(connectionType);
        }

        _connections[targetNodeId] = conn;

        pc.ondatachannel += (remoteChannel) => HandleRemoteDataChannel(targetNodeId, remoteChannel);

        var dc = await pc.createDataChannel(ControlChannel);
        RegisterAndBindHandshakeControlDataChannel(targetNodeId, dc);
        var tdc = await pc.createDataChannel(TimesyncChannel);
        RegisterAndBindTimeDataChannel(targetNodeId, tdc);

        var offer = pc.createOffer();
        await pc.setLocalDescription(offer);

        try
        {
            var payload = new RelaySignalPayload
            {
                OriginNodeId = _identityService.NodeId,
                TargetNodeId = targetNodeId,
                Sdp = offer.sdp,
                Type = RTCSdpType.offer.ToString(),
                HandShakeId = Guid.Empty
            };
            var envelope = new P2PPacketEnvelope
            {
                CommandType = "RelaySignal",
                SenderNodeId = _identityService.NodeId,
                TargetNodeId = targetNodeId,
                SerializedPayload = JsonSerializer.Serialize(payload),
                Ttl = 8,
            };

            foreach(var relayId in relayNodeIds)
            {
                var relayConn = GetConnection(relayId);
                if (relayConn == null) return null;
                SendMessageRaw(relayId, JsonSerializer.Serialize(envelope));
            }

            return conn;
        }
        catch
        {
            pc.close();
            _connections.TryRemove(targetNodeId, out _);
            return null;
        }
    }

    /// <summary>
    /// Authenticates a peer out-of-band via HTTP challenge exchanges.
    /// Handshaking is simplified to skip Signature 1/2/3 verification.
    /// </summary>
    public async Task<Guid?> TryHttpChallengeHandshakeAsync(string nodeId, string targetEndpoint)
    {
        //string targetEndpoint = PeerNode.GetEndpoint(node.SignalingUrl, node.SignalingAddress);
        if (string.IsNullOrEmpty(targetEndpoint)) return null;

        try
        {
            return await _challengeService.DoHttpConnectionChallenge(_http, nodeId, targetEndpoint);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[P2P Transport] HTTP challenge handshake failed for {NodeId}", nodeId);
        }
        return null;
    }

    public async Task RecoverDeadLinkAsync(string nodeId, string[] signalingEndpoints)
    {
        // A connection that failed its heartbeats is not kept: close it on our side right away
        await KillConnectionAsync(nodeId, "heartbeats failed");

        if (OurDialWins(nodeId))
        {
            _logger.LogInformation("[P2P Transport] Link to {NodeId} was dead; dialling it again (smaller id dials).", nodeId);
            await ConnectPeerAsync(nodeId, signalingEndpoints, "outbound");
            return;
        }

        // The other node has the smaller id and will dial us; give it time, then dial ourselves only if it didn't
        _logger.LogInformation("[P2P Transport] Link to {NodeId} was dead; waiting for it to dial us (it has the smaller id).", nodeId);
        await Task.Delay(TimeSpan.FromSeconds(15));
        if (_connections.TryGetValue(nodeId, out var back) && back.PeerConnection?.connectionState == RTCPeerConnectionState.connected)
            return;

        if (IsOffline(nodeId)) return;
        _logger.LogInformation("[P2P Transport] {NodeId} did not dial back within 15 s; dialling it ourselves.", nodeId);
        await ConnectPeerAsync(nodeId, signalingEndpoints, "outbound");
    }

    public bool HeardFromRecently(string nodeId, TimeSpan within) =>
        _lastInboundUtc.TryGetValue(nodeId, out var last) && DateTime.UtcNow - last <= within;

    private void ForgetInbound(string nodeId)
    {
        foreach (var key in _lastInboundChannel.Keys.Where(k => k.StartsWith(nodeId + "|", StringComparison.OrdinalIgnoreCase)).ToList())
            _lastInboundChannel.TryRemove(key, out _);
    }

    private bool IsSilent(string nodeId) =>
        !_lastInboundUtc.TryGetValue(nodeId, out var last) || DateTime.UtcNow - last > SilentConnectionLimit;

    public async Task<bool> KillConnectionAsync(string nodeId, string terminationReason = "")
    {
        foreach (var key in _lastInboundChannel.Keys.Where(k => k.StartsWith(nodeId + "|", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            if (_lastInboundChannel.TryRemove(key, out var inbound)) { try { inbound.close(); } catch { } }
        }

        if (_connections.TryRemove(nodeId, out var conn))
        {
            P2PModule.Services.Maintenance.ConsensusEventLog.WriteConnection(_settings.CurrentValue.DataStoragePath, _identityService.NodeId, "down", nodeId, reason: terminationReason);
            try
            {
                foreach (var dc in conn.DataChannels.Values) try { dc.close(); } catch { }
                conn.DataChannels.Clear();
                try { 
                    conn.PeerConnection?.close(); 
                } catch { }
            }
            catch { }

            //var peer = _peerTracker.GetPeer(nodeId);
            //if (peer != null)
            //{
            //    await _peerTracker.UpdatePeerAsync(peer);
            //}
            return true;
        }
        return false;
    }

    #region ConnectionMethods.WebRTC_Handover_Signaling_Handlers

    //http handshake required
    public async Task<string?> HandleIncomingSignalingHttpAsync(string senderNodeId, string offerSdp, Guid handshakeId)
    {
        if (handshakeId == Guid.Empty) return null;
        if (string.Equals(senderNodeId, _identityService.NodeId, StringComparison.OrdinalIgnoreCase)) return null;
        // A kept link key counts only right after a signed resume we accepted (the key alone travels in clear text)
        bool isHandshakeValid = _challengeService.IsRecentlyHandshaked(senderNodeId) || ConsumeResumeApproval(senderNodeId, handshakeId);
        if (!isHandshakeValid) return null;

        // Decide first, build the new connection after: a turned-away offer used to leave a stray connection
        // behind and wipe the reply channels of the connection we kept.
        if (!_connections.TryGetValue(senderNodeId, out var conn))
        {
            conn = new ActivePeerConnection
            {
                NodeId = senderNodeId,
                PeerConnection = null!,
                ConnectedAt = DateTime.UtcNow,
                LastActivity = DateTime.UtcNow,
                HandshakedAtUtc = DateTime.UtcNow,
            };
        }
        else
        if (_dialsInProgress.ContainsKey(senderNodeId) &&
            conn.PeerConnection != null &&
            conn.PeerConnection.connectionState != RTCPeerConnectionState.connected)
        {
            if (OurDialWins(senderNodeId))
            {
                // We are dialling it too and our id is smaller: our offer wins, theirs is turned away
                _logger.LogInformation("[P2P Transport] {NodeId} dialled us while we dial it; our dial wins (smaller id).", senderNodeId);
                return null;
            }
            // Its id is smaller: its offer wins, our own dial is dropped
            _logger.LogInformation("[P2P Transport] {NodeId} dialled us while we dial it; its dial wins (smaller id). Dropping ours.", senderNodeId);
            try { conn.PeerConnection.close(); } catch { }
        }
        else
        if(conn.PeerConnection.connectionState == RTCPeerConnectionState.connected)
        {
            // A connection that reports "connected" but has carried nothing from this node for a while is a
            // dead end left by crossed dials: both sides kept one that the other no longer reads, nothing got
            // through in either direction, and every re-dial was turned away here. Let the new dial replace it.
            if (!IsSilent(senderNodeId))
            {
                _logger.LogInformation("[P2P Transport] {NodeId} dialled again, but the current connection with it is up and carrying messages; it keeps using that one.", senderNodeId);
                return null;
            }

            _logger.LogWarning("[P2P Transport] {NodeId} dialled again and nothing has arrived from it for {Seconds}s on the current connection. Replacing it.",
                senderNodeId, (int)SilentConnectionLimit.TotalSeconds);
            try { conn.PeerConnection.close(); } catch { }
        }

        var configuration = new RTCConfiguration { iceServers = _settings.CurrentValue.StunServers?.Select(stun => new RTCIceServer { urls = stun }).ToList() ?? new List<RTCIceServer>() };
        var pc = new RTCPeerConnection(configuration);
        ForgetInbound(senderNodeId);   // a new connection: channels of the old one must not be used any more

        conn.PeerConnection = pc;
        _connections[senderNodeId] = conn;

        // Same dispatch as every other connection path. RegisterAndBindDataChannel treated every channel the
        // offerer opened later as a managed JSON channel, so unmanaged channels (file transfer "unmanaged:file:*")
        // never reached OnUnmanagedChannelReceived on the node that answered an HTTP-signalled connection:
        // the seeder never saw the request, sent no BITFIELD and served no slices.
        pc.ondatachannel += (remoteChannel) => HandleRemoteDataChannel(senderNodeId, remoteChannel);

        try
        {
            pc.setRemoteDescription(new RTCSessionDescriptionInit { sdp = offerSdp, type = RTCSdpType.offer });
            var answer = pc.createAnswer();
            await pc.setLocalDescription(answer);

            conn.IsHandshaked = true;
            conn.HandshakedAtUtc = DateTime.UtcNow;
            conn.ConnectedAt = DateTime.UtcNow;
            conn.LastActivity = DateTime.UtcNow;

            conn.ConnectionTypes.Add("inbound");
            //todo: throttle once per day
            await _ratingService.ApplyLocalReputationUpdateAsync(senderNodeId, baseAmount: 1);

            var peer = _peerTracker.GetPeer(senderNodeId);
            if (peer != null) conn.UpdateOffset(peer.ClockOffsetMs);

            await BumpPeerConnectivityAsync(senderNodeId);
            RememberLinkKey(senderNodeId, handshakeId);
            MarkOnline(senderNodeId);

            return answer.sdp;
        }
        catch { 
            pc.close(); 

            return null; 
        }
    }


    public async void HandleRelaySignaling(string senderNodeId, P2PPacketEnvelope envelope)
    {
        var signaling = SafeJson.Deserialize<RelaySignalPayload>(envelope.SerializedPayload);

        if (signaling != null)
        {
            if (signaling.Type == RTCSdpType.offer.ToString())
            {
                await HandleIncomingSignalingRelayedAsync(senderNodeId, signaling.OriginNodeId, signaling.Sdp);
                return;
            }
            else
            if (signaling.Type == RTCSdpType.answer.ToString())
            {
                await HandleIncomingSignalingAnswerAsync(signaling.OriginNodeId, signaling.Sdp);
                return;
            }
        }
    }

    //handshake is not required
    private async Task HandleIncomingSignalingRelayedAsync(string nodeFrom, string senderNodeId, string offerSdp)
    {
        var configuration = new RTCConfiguration { iceServers = _settings.CurrentValue.StunServers?.Select(stun => new RTCIceServer { urls = stun }).ToList() ?? new List<RTCIceServer>() };
        var pc = new RTCPeerConnection(configuration);
        ForgetInbound(senderNodeId);   // a new connection: channels of the old one must not be used any more

        if (!_connections.TryGetValue(senderNodeId, out var conn))
        {
            conn = new ActivePeerConnection
            {
                NodeId = senderNodeId,
                PeerConnection = pc,
                ConnectedAt = DateTime.UtcNow,
                LastActivity = DateTime.UtcNow,
                IsHandshaked = false
            };
            _connections[senderNodeId] = conn;
            conn.ConnectionTypes.Add("temporary");
        }

        pc.ondatachannel += (remoteChannel) => 
            HandleRemoteDataChannel(senderNodeId, remoteChannel);

        try
        {
            pc.setRemoteDescription(new RTCSessionDescriptionInit { sdp = offerSdp, type = RTCSdpType.offer });
            var answer = pc.createAnswer();
            await pc.setLocalDescription(answer);

            conn.ConnectedAt = DateTime.UtcNow;
            conn.LastActivity = DateTime.UtcNow;

            var payload = new RelaySignalPayload
            {
                OriginNodeId = _identityService.NodeId,
                TargetNodeId = senderNodeId,
                Sdp = answer.sdp,
                Type = RTCSdpType.answer.ToString(),
                HandShakeId = Guid.Empty
            };

            var envelope = new P2PPacketEnvelope
            {
                CommandType = "RelaySignal",
                SenderNodeId = _identityService.NodeId,
                TargetNodeId = senderNodeId,
                SerializedPayload = JsonSerializer.Serialize(payload),
                Ttl = 8,
            };

            SendMessageRaw(nodeFrom, JsonSerializer.Serialize(envelope), "p2p-control-channel");

        }
        catch {
            pc.close();
        }
    }

    private async Task HandleIncomingSignalingAnswerAsync(string targetNodeId, string answerSdp)
    {
        if (_connections.TryGetValue(targetNodeId, out var conn) && conn.PeerConnection != null 
            && (conn.PeerConnection.connectionState == RTCPeerConnectionState.@new || conn.PeerConnection.connectionState == RTCPeerConnectionState.connecting))
        {
            try { 

                conn.PeerConnection.setRemoteDescription(new RTCSessionDescriptionInit { sdp = answerSdp, type = RTCSdpType.answer });
                //todo:throttle once per day
                await _ratingService.ApplyLocalReputationUpdateAsync(targetNodeId, baseAmount: 1);
            }
            catch { await KillConnectionAsync(targetNodeId, "RemoteDescription setup crashed"); }
        }
    }

    #endregion

    #endregion

    public void Dispose()
    {
        _settingsChangeListener?.Dispose();
        _handshakeTimeoutTimer?.Dispose();
        _timeSyncPulseTimer?.Dispose();
        foreach (var conn in _connections.Values) try { conn.PeerConnection?.close(); } catch { }
        _connections.Clear();
    }
}

/// <summary>
/// "Do you still hold our link key?" Envelope = [link key | nonceA | time] encrypted with the TARGET's public
/// encryption key; Signature = sender's private signature key over (sender, target, time, hash of envelope).
/// </summary>
public class LinkResumeRequest
{
    public string SenderNodeId { get; set; } = string.Empty;
    public string TargetNodeId { get; set; } = string.Empty;
    public DateTime TimestampUtc { get; set; }
    public byte[]? Envelope { get; set; }
    public byte[]? Signature { get; set; }
}

/// <summary>
/// "Yes, the same one." Envelope = [nonceA | nonceB] encrypted with the REQUESTER's public encryption key;
/// Signature = answering node's private signature key over (answerer, requester, time, hash of envelope).
/// </summary>
public class LinkResumeResponse
{
    public string SenderNodeId { get; set; } = string.Empty;
    public DateTime TimestampUtc { get; set; }
    public byte[]? Envelope { get; set; }
    public byte[]? Signature { get; set; }
}
