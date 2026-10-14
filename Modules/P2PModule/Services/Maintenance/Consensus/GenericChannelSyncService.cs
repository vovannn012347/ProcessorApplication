using System.Text.Json;

using Common.Interfaces.Consensus;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using P2PModule.Interfaces;
using P2PModule.Models;
using P2PModule.Models.Network;
using P2PModule.Services.Connection;
using P2PModule.Services.Persistence;

namespace P2PModule.Services.Maintenance;

/// <summary>
/// Request payload for listing a channel's local record ids.
/// </summary>
public class ChannelRecordIdsRequest
{
    public string ChannelId { get; set; } = string.Empty;
}

/// <summary>
/// Response to ChannelRecordIdsRequest. Supported=false means the responder does not currently
/// advertise this channel (module not installed, or installed but not opted-in via settings) -
/// distinct from Supported=true with zero records.
/// </summary>
public class ChannelRecordIdsResponse
{
    public bool Supported { get; set; }
    public List<string> RecordIds { get; set; } = new();
}

/// <summary>
/// Request payload for fetching one record's raw payload from a channel.
/// </summary>
public class ChannelRecordFetchRequest
{
    public string ChannelId { get; set; } = string.Empty;
    public string RecordId { get; set; } = string.Empty;
}

/// <summary>
/// Response to ChannelRecordFetchRequest. PayloadBase64 is null when Found is false.
/// </summary>
public class ChannelRecordFetchResponse
{
    public bool Found { get; set; }
    public string? PayloadBase64 { get; set; }
}

/// <summary>
/// Outcome summary for one sync pass, returned to callers (admin UI, tests, etc) for visibility.
/// </summary>
public class ChannelSyncResult
{
    public bool Success { get; set; }
    public string? FailureReason { get; set; }
    public string ChannelId { get; set; } = string.Empty;
    public string? PeerNodeId { get; set; }
    public int RemoteRecordCount { get; set; }
    public int LocalRecordCount { get; set; }
    public int RecordsPulled { get; set; }
    public int RecordsRejected { get; set; }
    /// <summary>Missing ids the channel declined to fetch at all (SelectRecordsToFetchAsync) - no payload round-trip was spent on these.</summary>
    public int RecordsSkippedByChannel { get; set; }
}

public interface IGenericChannelSyncService
{
    /// <summary>
    /// Runs one sync pass for the given channel against a peer. If peerNodeId is not supplied, a
    /// candidate is picked from currently known peers that advertise this channel in their gossiped
    /// identity.
    /// </summary>
    Task<ChannelSyncResult> TriggerSyncAsync(string channelId, string? peerNodeId = null, CancellationToken ct = default);

    /// <summary>
    /// Ensures a continuous epidemic walk is running for this channel: seeds the walk queue from
    /// every currently known peer that advertises it and hasn't been visited yet, and starts the
    /// background walk loop if one isn't already running. The loop drains its queue (one
    /// TriggerSyncAsync pass per peer, tracking visited nodes the same way peer-trust's own walk
    /// does) and idles out after a few minutes of inactivity. Nothing runs unless this - or
    /// EnsurePeerQueuedAsync - is called; there is no internal timer here.
    /// </summary>
    Task EnsureChannelWalkRunningAsync(string channelId, CancellationToken ct = default);

    /// <summary>
    /// Queues one specific peer into a channel's walk, starting the walk loop for that channel if
    /// it isn't already running. This is what the discovery hook calls when a peer's gossiped
    /// identity reveals it supports a channel we also advertise.
    /// </summary>
    Task EnsurePeerQueuedAsync(string channelId, string peerNodeId, CancellationToken ct = default);

    /// <summary>Ids of channels whose walk loop is currently active - for admin/diagnostic visibility.</summary>
    IReadOnlyCollection<string> GetActiveWalkChannelIds();
}

/// <summary>
/// Drives real IConsensusChannel-based synchronization for any channel other than the built-in
/// peer-trust one (see ConsensusSessionManager/ConsensusProtocolHandler for that - deliberately
/// left untouched). This is a much lighter protocol than peer-trust's daily windowed signed
/// epidemic delta chains: compare local vs. remote record ids for a channel, pull what's missing,
/// and let the channel's own ResolveConflictAsync/ApplyRecordAsync decide what to keep. A channel
/// that needs stronger anti-tamper guarantees can bake its own signature checks into its payload
/// format and enforce them inside ResolveConflictAsync - this transport layer stays generic and
/// doesn't assume any particular trust model.
///
/// Registers its command handlers once at module start (not per-session, since there's no
/// long-lived session concept here - every request is a single atomic ask/answer) and rejects any
/// request for a channel this node doesn't currently actively advertise, with a clear logged NAK
/// rather than a silent drop or an accidental answer.
/// </summary>
public class GenericChannelSyncService : IGenericChannelSyncService, IHostedService
{
    private readonly IConsensusChannelRegistry _channelRegistry;
    private readonly IPeerTracker _peerTracker;
    private readonly IPeerConnectionManager _connectionManager;
    private readonly IP2PRouterService _routerService;
    private readonly INodeIdentityService _identity;
    private readonly ILogger<GenericChannelSyncService> _logger;

    private const string RecordIdsCommand = "ConsensusChannelRecordIds";
    private const string RecordFetchCommand = "ConsensusChannelRecordFetch";
    private static readonly TimeSpan WalkIdleTimeout = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Per-channel epidemic walk state. Deliberately separate from ConsensusSessionManager/
    /// ConsensusMergeSession - those carry disk-staging and chain-signature machinery this
    /// lightweight protocol has no use for. Just a visited set and a queue, guarded by _walkLock.
    /// </summary>
    private class ChannelWalkState
    {
        public readonly HashSet<string> VisitedNodes = new(StringComparer.OrdinalIgnoreCase);
        public readonly Queue<string> NodesToVisit = new();
        public CancellationTokenSource? LoopCts;
        public DateTime LastActivityUtc = DateTime.UtcNow;
    }

    private readonly object _walkLock = new();
    private readonly Dictionary<string, ChannelWalkState> _walkStateByChannel = new(StringComparer.OrdinalIgnoreCase);

    public GenericChannelSyncService(
        IConsensusChannelRegistry channelRegistry,
        IPeerTracker peerTracker,
        IPeerConnectionManager connectionManager,
        IP2PRouterService routerService,
        INodeIdentityService identity,
        ILogger<GenericChannelSyncService> logger)
    {
        _channelRegistry = channelRegistry;
        _peerTracker = peerTracker;
        _connectionManager = connectionManager;
        _routerService = routerService;
        _identity = identity;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("[Generic Channel Sync] Registering channel sync request handlers.");
        _routerService.RegisterCommandHandler(RecordIdsCommand, HandleRecordIdsRequestAsync);
        _routerService.RegisterCommandHandler(RecordFetchCommand, HandleRecordFetchRequestAsync);

        // Discovery hook: whenever a peer's identity is learned or refreshed, check whether it
        // advertises a channel we also advertise, and if so queue it into that channel's own
        // epidemic walk (starting the walk if needed). Peer-trust is excluded - it already has its
        // own role-based discovery (SeedVisitQueueFromPeerTracker in ConsensusProtocolHandler).
        _peerTracker.OnPeerNew += OnPeerDiscoveredOrUpdated;
        _peerTracker.OnPeerUpdate += OnPeerDiscoveredOrUpdated;

        // React when the user opts a channel in/out (settings), without waiting for peer gossip.
        _channelRegistry.AdvertisedChannelsChanged += OnAdvertisedChannelsChanged;

        return Task.CompletedTask;
    }

    /// <summary>
    /// Newly advertised channel: start its walk right away against peers already known to support
    /// it. No-longer-advertised channel: stop its walk and forget its visited set, so opting back in
    /// later starts a fresh walk. Peer-trust has its own protocol and is never walked here.
    /// </summary>
    private void OnAdvertisedChannelsChanged(AdvertisedChannelsChange change)
    {
        foreach (var channelId in change.Added)
        {
            if (channelId.Equals(PeerTrustConsensusChannel.CHANNEL_ID, StringComparison.OrdinalIgnoreCase)) continue;
            _ = EnsureChannelWalkRunningAsync(channelId);
        }

        foreach (var channelId in change.Removed)
        {
            lock (_walkLock)
            {
                if (_walkStateByChannel.Remove(channelId, out var state))
                {
                    state.LoopCts?.Cancel();
                    _logger.LogInformation("[Generic Channel Sync] Channel '{ChannelId}' no longer advertised - walk stopped.", channelId);
                }
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _peerTracker.OnPeerNew -= OnPeerDiscoveredOrUpdated;
        _peerTracker.OnPeerUpdate -= OnPeerDiscoveredOrUpdated;
        _channelRegistry.AdvertisedChannelsChanged -= OnAdvertisedChannelsChanged;

        lock (_walkLock)
        {
            foreach (var state in _walkStateByChannel.Values)
            {
                state.LoopCts?.Cancel();
            }
        }

        _routerService.UnregisterCommandHandler(RecordIdsCommand);
        _routerService.UnregisterCommandHandler(RecordFetchCommand);
        return Task.CompletedTask;
    }

    private void OnPeerDiscoveredOrUpdated(PeerNodeRuntime peer)
    {
        var theirNodeId = peer?.Identity?.NodeId;
        if (string.IsNullOrEmpty(theirNodeId) || theirNodeId.Equals(_identity.NodeId, StringComparison.OrdinalIgnoreCase))
            return;

        var theirChannels = peer!.Identity.ConsensusChannels;
        if (theirChannels == null || theirChannels.Count == 0) return;

        foreach (var channelId in _channelRegistry.AdvertisedChannelIds)
        {
            if (channelId.Equals(PeerTrustConsensusChannel.CHANNEL_ID, StringComparison.OrdinalIgnoreCase)) continue;
            if (!theirChannels.Contains(channelId, StringComparer.OrdinalIgnoreCase)) continue;

            _ = EnsurePeerQueuedAsync(channelId, theirNodeId);
        }
    }

    public async Task<ChannelSyncResult> TriggerSyncAsync(string channelId, string? peerNodeId = null, CancellationToken ct = default)
    {
        var result = new ChannelSyncResult { ChannelId = channelId };

        var channel = _channelRegistry.Get(channelId);
        if (channel == null)
        {
            result.FailureReason = "CHANNEL_NOT_INSTALLED";
            _logger.LogWarning("[Generic Channel Sync] Cannot sync channel '{ChannelId}' - not installed on this node.", channelId);
            return result;
        }

        if (!_channelRegistry.AdvertisedChannelIds.Contains(channelId, StringComparer.OrdinalIgnoreCase))
        {
            result.FailureReason = "CHANNEL_NOT_ADVERTISED";
            _logger.LogWarning("[Generic Channel Sync] Refusing to sync channel '{ChannelId}' - installed but not opted-in via P2PSettings.AdvertisedConsensusChannels.", channelId);
            return result;
        }

        if (string.IsNullOrEmpty(peerNodeId))
        {
            peerNodeId = PickCandidatePeer(channelId);
            if (peerNodeId == null)
            {
                result.FailureReason = "NO_CANDIDATE_PEER";
                _logger.LogInformation("[Generic Channel Sync] No known peer currently advertises channel '{ChannelId}'. Nothing to sync against.", channelId);
                return result;
            }
        }

        result.PeerNodeId = peerNodeId;

        if (!await EnsurePeerConnectionAsync(peerNodeId, ct))
        {
            result.FailureReason = "CONNECTION_FAILED";
            _logger.LogWarning("[Generic Channel Sync] Could not establish connection to {PeerNodeId} for channel '{ChannelId}' sync.", peerNodeId, channelId);
            return result;
        }

        var localIds = new HashSet<string>(await channel.GetLocalRecordIdsAsync(ct), StringComparer.Ordinal);
        result.LocalRecordCount = localIds.Count;

        var idsResponse = await SendRecordIdsRequestAsync(peerNodeId, channelId, ct);
        if (idsResponse == null)
        {
            result.FailureReason = "NO_RESPONSE";
            return result;
        }
        if (!idsResponse.Supported)
        {
            result.FailureReason = "PEER_DOES_NOT_ADVERTISE_CHANNEL";
            _logger.LogInformation("[Generic Channel Sync] Peer {PeerNodeId} no longer advertises channel '{ChannelId}'. Skipping.", peerNodeId, channelId);
            return result;
        }

        result.RemoteRecordCount = idsResponse.RecordIds.Count;
        var missingLocally = idsResponse.RecordIds.Where(id => !localIds.Contains(id)).ToList();
        var stillPresent = idsResponse.RecordIds.Where(id => localIds.Contains(id)).ToList();

        _logger.LogInformation("[Generic Channel Sync] Channel '{ChannelId}' vs {PeerNodeId}: local={LocalCount}, remote={RemoteCount}, missing locally={MissingCount}, confirmed present={StillPresentCount}.",
            channelId, peerNodeId, localIds.Count, idsResponse.RecordIds.Count, missingLocally.Count, stillPresent.Count);

        if (stillPresent.Count > 0)
        {
            try { await channel.MarkRecordsStillPresentAsync(stillPresent, ct); }
            catch (Exception ex) { _logger.LogWarning(ex, "[Generic Channel Sync] MarkRecordsStillPresentAsync threw for channel '{ChannelId}'.", channelId); }
        }

        // Let the channel apply admission limits (e.g. per-host caps) from ids alone, so we don't
        // spend a payload round-trip on records it would only reject.
        IReadOnlyList<string> toFetch = missingLocally;
        try
        {
            toFetch = await channel.SelectRecordsToFetchAsync(missingLocally, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Generic Channel Sync] SelectRecordsToFetchAsync threw for channel '{ChannelId}'; fetching all candidates.", channelId);
        }
        result.RecordsSkippedByChannel = missingLocally.Count - toFetch.Count;

        foreach (var recordId in toFetch)
        {
            if (ct.IsCancellationRequested) break;

            var fetch = await SendRecordFetchRequestAsync(peerNodeId, channelId, recordId, ct);
            if (fetch == null || !fetch.Found || string.IsNullOrEmpty(fetch.PayloadBase64))
            {
                continue;
            }

            byte[] payload;
            try
            {
                payload = Convert.FromBase64String(fetch.PayloadBase64);
            }
            catch (FormatException)
            {
                _logger.LogWarning("[Generic Channel Sync] Malformed base64 payload for record {RecordId} on channel '{ChannelId}' from {PeerNodeId}.", recordId, channelId, peerNodeId);
                result.RecordsRejected++;
                continue;
            }

            var resolution = await channel.ResolveConflictAsync(recordId, payload, null, ct);
            if (resolution == ConsensusMergeResolution.AcceptIncoming)
            {
                await channel.ApplyRecordAsync(recordId, payload, ct);
                result.RecordsPulled++;
            }
            else
            {
                result.RecordsRejected++;
            }
        }

        result.Success = true;
        _logger.LogInformation("[Generic Channel Sync] Sync of channel '{ChannelId}' with {PeerNodeId} complete: pulled={Pulled}, rejected={Rejected}.",
            channelId, peerNodeId, result.RecordsPulled, result.RecordsRejected);

        return result;
    }

    private async Task<string> HandleRecordIdsRequestAsync(string senderNodeId, P2PPacketEnvelope frame, CancellationToken ct)
    {
        var response = new ChannelRecordIdsResponse();

        try
        {
            var req = string.IsNullOrEmpty(frame.SerializedPayload)
                ? null
                : JsonSerializer.Deserialize<ChannelRecordIdsRequest>(frame.SerializedPayload);

            if (req == null || string.IsNullOrEmpty(req.ChannelId))
            {
                return JsonSerializer.Serialize(response);
            }

            if (!_channelRegistry.AdvertisedChannelIds.Contains(req.ChannelId, StringComparer.OrdinalIgnoreCase))
            {
                _logger.LogInformation("[Generic Channel Sync] Rejected record-id probe from {SenderNodeId} for non-advertised channel '{ChannelId}'.", senderNodeId, req.ChannelId);
                return JsonSerializer.Serialize(response);
            }

            var channel = _channelRegistry.Get(req.ChannelId);
            if (channel == null)
            {
                return JsonSerializer.Serialize(response);
            }

            response.Supported = true;
            response.RecordIds = (await channel.GetLocalRecordIdsAsync(ct)).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Generic Channel Sync] Error handling record-id request from {SenderNodeId}.", senderNodeId);
        }

        return JsonSerializer.Serialize(response);
    }

    private async Task<string> HandleRecordFetchRequestAsync(string senderNodeId, P2PPacketEnvelope frame, CancellationToken ct)
    {
        var response = new ChannelRecordFetchResponse();

        try
        {
            var req = string.IsNullOrEmpty(frame.SerializedPayload)
                ? null
                : JsonSerializer.Deserialize<ChannelRecordFetchRequest>(frame.SerializedPayload);

            if (req == null || string.IsNullOrEmpty(req.ChannelId) || string.IsNullOrEmpty(req.RecordId))
            {
                return JsonSerializer.Serialize(response);
            }

            if (!_channelRegistry.AdvertisedChannelIds.Contains(req.ChannelId, StringComparer.OrdinalIgnoreCase))
            {
                _logger.LogInformation("[Generic Channel Sync] Rejected record fetch from {SenderNodeId} for non-advertised channel '{ChannelId}'.", senderNodeId, req.ChannelId);
                return JsonSerializer.Serialize(response);
            }

            var channel = _channelRegistry.Get(req.ChannelId);
            var payload = channel == null ? null : await channel.GetRecordPayloadAsync(req.RecordId, ct);

            if (payload != null)
            {
                response.Found = true;
                response.PayloadBase64 = Convert.ToBase64String(payload);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Generic Channel Sync] Error handling record fetch request from {SenderNodeId}.", senderNodeId);
        }

        return JsonSerializer.Serialize(response);
    }

    private async Task<ChannelRecordIdsResponse?> SendRecordIdsRequestAsync(string targetNodeId, string channelId, CancellationToken ct)
    {
        try
        {
            var req = new ChannelRecordIdsRequest { ChannelId = channelId };
            var result = await _routerService.SendRequestAsync(
                targetNodeId, RecordIdsCommand, JsonSerializer.Serialize(req),
                new RouteSettings { Timeout = TimeSpan.FromSeconds(20), CancellationToken = ct });

            if (!result.IsSuccess || string.IsNullOrEmpty(result.Payload)) return null;
            return JsonSerializer.Deserialize<ChannelRecordIdsResponse>(result.Payload);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Generic Channel Sync] Record-id request to {TargetNodeId} for channel '{ChannelId}' failed.", targetNodeId, channelId);
            return null;
        }
    }

    private async Task<ChannelRecordFetchResponse?> SendRecordFetchRequestAsync(string targetNodeId, string channelId, string recordId, CancellationToken ct)
    {
        try
        {
            var req = new ChannelRecordFetchRequest { ChannelId = channelId, RecordId = recordId };
            var result = await _routerService.SendRequestAsync(
                targetNodeId, RecordFetchCommand, JsonSerializer.Serialize(req),
                new RouteSettings { Timeout = TimeSpan.FromSeconds(20), CancellationToken = ct });

            if (!result.IsSuccess || string.IsNullOrEmpty(result.Payload)) return null;
            return JsonSerializer.Deserialize<ChannelRecordFetchResponse>(result.Payload);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Generic Channel Sync] Record fetch of {RecordId} from {TargetNodeId} for channel '{ChannelId}' failed.", recordId, targetNodeId, channelId);
            return null;
        }
    }

    private string? PickCandidatePeer(string channelId)
    {
        var candidates = GetCandidatePeers(channelId);
        if (candidates.Count == 0) return null;

        // Random pick rather than always the same peer, for basic epidemic spread properties.
        return candidates[Random.Shared.Next(candidates.Count)];
    }

    private List<string> GetCandidatePeers(string channelId)
    {
        return _peerTracker.GetLastSeenNodes(PeerTracker.DefaultNodeComparer)
            .Where(p => p.Identity?.NodeId != null
                        && !p.Identity.NodeId.Equals(_identity.NodeId, StringComparison.OrdinalIgnoreCase)
                        && p.Identity.ConsensusChannels != null
                        && p.Identity.ConsensusChannels.Contains(channelId, StringComparer.OrdinalIgnoreCase))
            .Select(p => p.Identity!.NodeId)
            .ToList();
    }

    public Task EnsureChannelWalkRunningAsync(string channelId, CancellationToken ct = default)
    {
        if (!_channelRegistry.AdvertisedChannelIds.Contains(channelId, StringComparer.OrdinalIgnoreCase))
        {
            _logger.LogInformation("[Generic Channel Sync] Not starting walk for '{ChannelId}' - not currently advertised.", channelId);
            return Task.CompletedTask;
        }

        ChannelWalkState state;
        bool needsStart;

        lock (_walkLock)
        {
            if (!_walkStateByChannel.TryGetValue(channelId, out state!))
            {
                state = new ChannelWalkState();
                _walkStateByChannel[channelId] = state;
            }

            foreach (var candidate in GetCandidatePeers(channelId))
            {
                if (!state.VisitedNodes.Contains(candidate) && !state.NodesToVisit.Contains(candidate))
                {
                    state.NodesToVisit.Enqueue(candidate);
                }
            }
            state.LastActivityUtc = DateTime.UtcNow;

            needsStart = state.LoopCts == null;
            if (needsStart)
            {
                state.LoopCts = new CancellationTokenSource();
            }
        }

        if (needsStart)
        {
            var loopToken = state.LoopCts!.Token;
            _ = Task.Run(() => RunChannelWalkLoopAsync(channelId, state, loopToken), CancellationToken.None);
            _logger.LogInformation("[Generic Channel Sync] Started epidemic walk loop for channel '{ChannelId}'.", channelId);
        }

        return Task.CompletedTask;
    }

    public Task EnsurePeerQueuedAsync(string channelId, string peerNodeId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(peerNodeId)) return Task.CompletedTask;
        if (!_channelRegistry.AdvertisedChannelIds.Contains(channelId, StringComparer.OrdinalIgnoreCase))
        {
            return Task.CompletedTask;
        }

        ChannelWalkState state;
        bool needsStart;

        lock (_walkLock)
        {
            if (!_walkStateByChannel.TryGetValue(channelId, out state!))
            {
                state = new ChannelWalkState();
                _walkStateByChannel[channelId] = state;
            }

            if (!state.VisitedNodes.Contains(peerNodeId) && !state.NodesToVisit.Contains(peerNodeId))
            {
                state.NodesToVisit.Enqueue(peerNodeId);
                _logger.LogInformation("[Generic Channel Sync] Discovery hook queued {PeerNodeId} for channel '{ChannelId}'.", peerNodeId, channelId);
            }
            state.LastActivityUtc = DateTime.UtcNow;

            needsStart = state.LoopCts == null;
            if (needsStart)
            {
                state.LoopCts = new CancellationTokenSource();
            }
        }

        if (needsStart)
        {
            var loopToken = state.LoopCts!.Token;
            _ = Task.Run(() => RunChannelWalkLoopAsync(channelId, state, loopToken), CancellationToken.None);
            _logger.LogInformation("[Generic Channel Sync] Started epidemic walk loop for channel '{ChannelId}' (triggered by discovery).", channelId);
        }

        return Task.CompletedTask;
    }

    public IReadOnlyCollection<string> GetActiveWalkChannelIds()
    {
        lock (_walkLock)
        {
            return _walkStateByChannel.Where(kv => kv.Value.LoopCts != null).Select(kv => kv.Key).ToList();
        }
    }

    /// <summary>
    /// Drains a channel's visit queue one peer at a time - same visited/queue pattern as peer-trust's
    /// own workflow loop, just without any disk staging. Idles out and releases the loop after
    /// WalkIdleTimeout of no queue activity, so EnsureChannelWalkRunningAsync/EnsurePeerQueuedAsync
    /// can cleanly restart it later rather than it running forever.
    /// </summary>
    private async Task RunChannelWalkLoopAsync(string channelId, ChannelWalkState state, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                string? next = null;
                lock (_walkLock)
                {
                    if (state.NodesToVisit.Count > 0)
                    {
                        next = state.NodesToVisit.Dequeue();
                    }
                }

                if (next == null)
                {
                    bool idleTooLong;
                    lock (_walkLock)
                    {
                        idleTooLong = DateTime.UtcNow - state.LastActivityUtc >= WalkIdleTimeout;
                    }

                    if (idleTooLong)
                    {
                        lock (_walkLock) { state.LoopCts = null; }
                        _logger.LogInformation("[Generic Channel Sync] Walk loop for channel '{ChannelId}' idled out. Visited {Count} nodes this run.", channelId, state.VisitedNodes.Count);
                        return;
                    }

                    await Task.Delay(TimeSpan.FromSeconds(5), ct);
                    continue;
                }

                if (state.VisitedNodes.Contains(next)) continue;

                await TriggerSyncAsync(channelId, next, ct);

                lock (_walkLock)
                {
                    state.VisitedNodes.Add(next);
                    state.LastActivityUtc = DateTime.UtcNow;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(250), ct);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("[Generic Channel Sync] Walk loop for channel '{ChannelId}' canceled.", channelId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Generic Channel Sync] Unhandled error in walk loop for channel '{ChannelId}'.", channelId);
            lock (_walkLock) { state.LoopCts = null; }
        }
    }

    private async Task<bool> EnsurePeerConnectionAsync(string targetNodeId, CancellationToken ct)
    {
        var activeConn = _connectionManager.GetConnection(targetNodeId);
        if (activeConn != null && activeConn.PeerConnection != null &&
            activeConn.PeerConnection.connectionState == SIPSorcery.Net.RTCPeerConnectionState.connected)
        {
            return true;
        }

        var peer = _peerTracker.GetPeer(targetNodeId);
        string[] signalingEndpoints = peer?.Identity?.SignalingEndpoints?.ToArray() ?? Array.Empty<string>();

        var newConn = await _connectionManager.ConnectPeerAsync(targetNodeId, signalingEndpoints, "outbound");
        return newConn != null;
    }
}
