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

        public bool IsError => string.IsNullOrEmpty(Error);
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

        _settingsChangeListener = _settings.OnChange((settings, name) => UpdateSettings());
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
    public bool SendMessageRaw(string nodeId, string payload, string channelLabel = "p2p-control-channel")
    {
        if (_connections.TryGetValue(nodeId, out var conn))
        {
            if (conn.DataChannels.TryGetValue(channelLabel, out var dc) && dc.readyState == RTCDataChannelState.open)
            {
                dc.send(payload);
                //conn.LastActivity = DateTime.UtcNow;
                return true;
            }
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
                        existingChannel.readyState == RTCDataChannelState.open)
                    {
                        return new CreateChannelResult
                        {
                            Channel = existingChannel
                        };
                    }

                    string wireLabel = isUnmanaged
                        ? (channelLabel.StartsWith("unmanaged:", StringComparison.OrdinalIgnoreCase) ? channelLabel : $"unmanaged:{channelLabel}")
                        : channelLabel;

                    var dc = await conn.PeerConnection.createDataChannel(wireLabel);
                    RegisterAndBindDataChannel(nodeId, dc, isUnmanaged: isUnmanaged);

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
            conn.DataChannels.TryRemove(cleanLabel, out _);
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
                conn.DataChannels.TryRemove(cleanLabel, out _);
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
                activeConn.DataChannels.TryRemove(label, out _);
        };

        if (_connections.TryGetValue(nodeId, out var conn))
        {
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
            if (_connections.TryGetValue(nodeId, out var activeConn)) activeConn.DataChannels.TryRemove(label, out _);
        };

        dc.onmessage += (channel, protocol, data) => UpdateConnectionActivity(nodeId);
        dc.onmessage += (channel, protocol, data) => HandleTimeSyncPacket(nodeId, data);
    }

    #endregion

    /// <summary>
    /// Probes a specific endpoint for cryptographic PONG confirmation.
    /// </summary>
    public async Task<string> GetEndpointActiveAsync(string endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint)) return string.Empty;

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
    public async Task<ActivePeerConnection?> ConnectPeerAsync(string nodeId, string[] signalingEndpoints, string connectionType)
    {
        _logger.LogInformation("[P2P Transport] Establishing WebRTC session with {NodeId}.", nodeId);

        if (_connections.TryGetValue(nodeId, out var existingConn) && 
            existingConn.PeerConnection.connectionState == RTCPeerConnectionState.connected)
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

        // HTTP-First Flow
        //ping for active url
        List<string> activeEndpoints = new();
        foreach (var endpoint in signalingEndpoints)
        {
            var target = string.Empty;
            if (!string.IsNullOrEmpty(target = await GetEndpointActiveAsync(endpoint)))
            {
                activeEndpoints.Add(target);
            }
        }

        if (activeEndpoints.Any(e => !string.IsNullOrEmpty(e)))
        {
            //http cnnection available
            Guid? handshakeId = null;
            var target = string.Empty;
            foreach (var targetUrl in activeEndpoints)
            {
                handshakeId = await TryHttpChallengeHandshakeAsync(nodeId, targetUrl);
                if (handshakeId != null && handshakeId != Guid.Empty)
                {
                    target = targetUrl;
                    break;
                }
            }

            if(handshakeId != null)
            {
                var configuration = new RTCConfiguration { iceServers = _settings.CurrentValue.StunServers?.Select(stun => new RTCIceServer { urls = stun }).ToList() ?? new List<RTCIceServer>() };
                var pc = new RTCPeerConnection(configuration);

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
                    conn.PeerConnection?.close();
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
                            pc.setRemoteDescription(new RTCSessionDescriptionInit { sdp = answer.Sdp, type = RTCSdpType.answer });

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

                pc.close();
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
            return await InitiateRelayedConnectionAsync(nodeId, fallbackGateways.ToArray(), connectionType);
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

    public async Task<bool> KillConnectionAsync(string nodeId, string terminationReason = "")
    {
        if (_connections.TryRemove(nodeId, out var conn))
        {
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
        bool isHandshakeValid = _challengeService.IsRecentlyHandshaked(senderNodeId);
        if (!isHandshakeValid) return null;

        var configuration = new RTCConfiguration { iceServers = _settings.CurrentValue.StunServers?.Select(stun => new RTCIceServer { urls = stun }).ToList() ?? new List<RTCIceServer>() };
        var pc = new RTCPeerConnection(configuration);

        if (!_connections.TryGetValue(senderNodeId, out var conn))
        {
            conn = new ActivePeerConnection
            {
                NodeId = senderNodeId,
                PeerConnection = pc,
                ConnectedAt = DateTime.UtcNow,
                LastActivity = DateTime.UtcNow,
                HandshakedAtUtc = DateTime.UtcNow,
            };
        }
        else
        if(conn.PeerConnection.connectionState == RTCPeerConnectionState.connected)
        {
            //what ARE you thinking?
            return null;
        }

        conn.PeerConnection = pc;
        _connections[senderNodeId] = conn;

        pc.ondatachannel += (remoteChannel) => RegisterAndBindDataChannel(senderNodeId, remoteChannel);

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