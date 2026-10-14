// [full-implementation]
using System.Text.Json;

using Common.Code;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using P2PModule.Configuration;
using P2PModule.Interfaces;
using P2PModule.Models;
using P2PModule.Models.Network;
using P2PModule.Services.Connection;
using P2PModule.Services.Maintenance;
using P2PModule.Services.Persistence;
using ProcessorApplication.Utils;

namespace P2PModule.Services.Discovery;


/// <summary>
/// Orchestrates mesh constraints, slot deficit allocations, keep-alive pings, and 
/// progressive sharded consensus updates using the ConsensusMergeEngine abstraction.
/// </summary>
public class ManagementService : BackgroundService, IMeshManagementService, IDisposable
{
    public const string OutboundConnectionType = "outbound";

    private readonly ILogger<ManagementService> _logger;
    private readonly IOptionsMonitor<P2PSettings> _settings;
    private readonly HttpClient _http;
    private readonly IP2PRouterService _p2pRouter;
    private readonly IPeerConnectionManager _connectionManager;
    private readonly INodeIdentityService _identity;
    private readonly IPeerTracker _peerTracker;
    private readonly IConsensusStore _consensusStore;
    private readonly IConsensusMergeEngine _mergeEngine;

    private Timer? _heartbeatTimer;
    private IDisposable? _settingsChangeListener;

    public ManagementService(
       ILogger<ManagementService> logger,
       IOptionsMonitor<P2PSettings> settings,
       HttpClient http,
       IP2PRouterService p2pRouter,
       IPeerConnectionManager connectionManager,
       INodeIdentityService identity,
       IPeerTracker peerTracker,
       IConsensusStore consensusStore,
       IConsensusMergeEngine mergeEngine)
    {
        _identity = identity;
        _peerTracker = peerTracker;
        _consensusStore = consensusStore;
        _connectionManager = connectionManager;
        _p2pRouter = p2pRouter;
        _http = http;
        _logger = logger;
        _settings = settings;
        _mergeEngine = mergeEngine;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // BINDING: Register default command handlers inside Router's registry
        _p2pRouter.RegisterCommandHandler("Ping", async (senderId, envelope, token) =>
        {
            return "{\"Status\":\"PONG\"}";
        });

        _p2pRouter.RegisterCommandHandler("RequestConsensusWakeUpSegmented", RequestConsensusWakeUpSegmented_Handler);


        double periodMs = _settings.CurrentValue?.KeepAlivePeriodMs > 0 ? _settings.CurrentValue.KeepAlivePeriodMs : 30000;
        _heartbeatTimer = new Timer(
            _ => _ = PerformKeepAliveHeartbeatsAsync(),
            null,
            TimeSpan.FromMilliseconds(periodMs),
            TimeSpan.FromMilliseconds(periodMs));

        // Kept in _settingsChangeListener so Dispose actually unsubscribes (it was discarded before).
        _settingsChangeListener = _settings.OnChangeDebounced(UpdateSettings);

        return Task.CompletedTask;
    }

    private async Task PerformKeepAliveHeartbeatsAsync()
    {
        try
        {
            var activeConnections = _connectionManager.GetActiveConnections().ToList();
            foreach (var conn in activeConnections)
            {
                if (conn != null && !string.IsNullOrEmpty(conn.NodeId))
                {
                    await SendHeartbeatAsync(conn.NodeId);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[P2P Management] Exception in keep-alive heartbeat execution loop.");
        }
    }

    private void UpdateSettings(P2PSettings settings)
    {
        double periodMs = _settings.CurrentValue?.KeepAlivePeriodMs > 0 ? _settings.CurrentValue.KeepAlivePeriodMs : 3000;

        _heartbeatTimer?.Change(TimeSpan.FromMilliseconds(periodMs), TimeSpan.FromMilliseconds(periodMs));
    }

    public async Task PerformInitialHandshakeAsync()
    {
        var allCandidates = _peerTracker.GetAvailableNodes(PeerTracker.DefaultNodeComparer).ToList();

        if (!allCandidates.Any()) return;

        var trackers = allCandidates.Where(n => n.Identity.Roles.Contains("tracker")).ToList();

        int connections = 0;
        // 1. Prioritize connecting to a bootstrap Tracker authority node
        foreach (var tracker in trackers)
        {
            var connected = await _connectionManager.ConnectPeerAsync(
                tracker.Identity.NodeId,
                tracker.Identity.SignalingEndpoints.ToArray(),
                OutboundConnectionType);

            if (connected != null)
            {
                ++connections;
                break;
            }
            allCandidates.Remove(tracker);
        }

        // 2. Fulfill outbound slots selecting unconnected diverse candidate profiles
        int connectionsNeeded = _settings.CurrentValue.TargetOutboundConnections;
        foreach (var node in allCandidates)
        {
            var connected = await _connectionManager.ConnectPeerAsync(
                node.Identity.NodeId,
                node.Identity.SignalingEndpoints.ToArray(),
                OutboundConnectionType);
            if (connected != null)
            {
                ++connections;
            }
            if (connections >= connectionsNeeded) break;
        }
    }

    public async Task MaintainOutboundConnectionsAsync()
    {
        _logger.LogInformation("[P2P Mesh Management] Commencing partial-mesh topology check.");

        var activeConnections = _connectionManager.GetActiveConnections().ToList();

        var inboundConnections = activeConnections
            .Where(c => c.ConnectionTypes.Contains("inbound") &&
                        c.PeerConnection != null &&
                        c.PeerConnection.connectionState == SIPSorcery.Net.RTCPeerConnectionState.connected)
            .ToList();

        var currentOutbounds = _connectionManager.GetActiveConnections()
            .Where(c => c.ConnectionTypes.Contains(OutboundConnectionType) &&
                        c.PeerConnection != null &&
                        c.PeerConnection.connectionState == SIPSorcery.Net.RTCPeerConnectionState.connected)
            .ToList();

        // Repair tracker links if missing
        bool hasTrackerOutbound = currentOutbounds.Any(o =>
            _peerTracker.GetPeer(o.NodeId)?.Identity.Roles.Contains("tracker") == true);

        if (!hasTrackerOutbound)
        {
            var trackers = _peerTracker.GetAvailableNodes(PeerTracker.DefaultNodeComparer)
                .Where(n => n.Identity.Roles.Contains("tracker") && n.Identity.NodeId != _identity.NodeId)
                .ToList();

            foreach (var tracker in trackers)
            {
                if (currentOutbounds.Any(o => o.NodeId == tracker.Identity.NodeId)) continue;

                var connected = await _connectionManager.ConnectPeerAsync(
                    tracker.Identity.NodeId,
                    tracker.Identity.SignalingEndpoints.ToArray(),
                    OutboundConnectionType);
                if (connected != null) break;
            }
        }

        // Fulfill remaining target outbound slots
        currentOutbounds = _connectionManager.GetActiveConnections()
            .Where(c => c.ConnectionTypes.Contains(OutboundConnectionType) &&
                        c.PeerConnection != null &&
                        c.PeerConnection.connectionState == SIPSorcery.Net.RTCPeerConnectionState.connected)
            .ToList();

        int targetConnections = _settings.CurrentValue.TargetOutboundConnections;
        if (currentOutbounds.Count < targetConnections)
        {
            int connectionsNeeded = targetConnections - currentOutbounds.Count;
            var candidates = _peerTracker.GetLastSeenNodes(PeerTracker.DefaultNodeComparer).ToList();
            if (candidates.Count == 0)
            {
                candidates = _peerTracker.GetAvailableNodes(PeerTracker.DefaultNodeComparer).ToList();
            }

            string myNodeId = _identity.NodeId;
            var activeInboundIds = inboundConnections.Select(i => i.NodeId).ToHashSet();
            var activeOutboundIds = currentOutbounds.Select(o => o.NodeId).ToHashSet();

            var preferredCandidates = candidates.Where(c =>
                c.Identity.NodeId != myNodeId &&
                !activeOutboundIds.Contains(c.Identity.NodeId) &&
                !activeInboundIds.Contains(c.Identity.NodeId)).ToList();

            var fallbackCandidates = candidates.Where(c =>
                c.Identity.NodeId != myNodeId &&
                !activeOutboundIds.Contains(c.Identity.NodeId) &&
                activeInboundIds.Contains(c.Identity.NodeId)).ToList();

            var prioritizedPool = preferredCandidates.Concat(fallbackCandidates).ToList();

            foreach (var candidate in prioritizedPool)
            {
                var connected = await _connectionManager.ConnectPeerAsync(
                    candidate.Identity.NodeId,
                    candidate.Identity.SignalingEndpoints.ToArray(),
                    OutboundConnectionType);
                if (connected != null)
                {
                    --connectionsNeeded;
                    if (connectionsNeeded <= 0) break;
                }
            }
        }
    }

    public async Task<bool> DiscoverNewConsensus()
    {
        _logger.LogInformation("[P2P Mesh Management] Launching progressive chunk-based consensus sync folder sync...");
        string syncDate = DateTime.UtcNow.ToString("yyyy-MM-dd");

        // Initialize disk staging via ConsensusMergeEngine
        ConsensusMergeOperation stagingOp = await _mergeEngine.CreateStagingOperationAsync(syncDate);

        var activeConnections = _connectionManager.GetActiveConnections().ToList();
        var trackers = activeConnections.Where(c =>
            _peerTracker.GetPeer(c.NodeId)?.Identity.Roles.Contains("tracker") == true).ToList();

        var targets = trackers.Any() ? trackers :
            activeConnections.OrderByDescending(c => _peerTracker.GetPeer(c.NodeId)?.TrustScore ?? 0).ToList();

        int consensusSyncedAny = 0;

        foreach (var target in targets)
        {
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            int configuredTimeout = _settings.CurrentValue.DefaultRequestTimeoutSeconds > 0 ? _settings.CurrentValue.DefaultRequestTimeoutSeconds : 10;
            var interChunkTimeout = TimeSpan.FromSeconds(configuredTimeout / 2 > 0 ? configuredTimeout / 2 : 5);

            using var cts = new CancellationTokenSource();

            try
            {
                await _p2pRouter.SendRequestProgressiveAsync(
                    target.NodeId,
                    "RequestConsensusWakeUpSegmented",
                    JsonSerializer.Serialize(new { BatchSize = 40 }),
                    async (chunkJson, error, progress, finished) =>
                    {
                        if (!string.IsNullOrEmpty(error))
                        {
                            tcs.TrySetException(new Exception(error));
                            return;
                        }

                        if (!string.IsNullOrEmpty(chunkJson))
                        {
                            var docSegment = JsonSerializer.Deserialize<NodesTransferContainer>(chunkJson);
                            if (docSegment != null && docSegment.Nodes != null)
                            {
                                await _mergeEngine.StageIncomingNodeSetAsync(stagingOp, target.NodeId, docSegment.Nodes);
                                Interlocked.Increment(ref consensusSyncedAny);
                            }
                        }

                        if (finished) tcs.TrySetResult(true);
                    },
                    new RouteSettings
                    {
                        Timeout = interChunkTimeout,
                        CancellationToken = cts.Token
                    });

                await tcs.Task;
                cts.Cancel();
                await _mergeEngine.FinalizeIterationAsync(stagingOp);
                Interlocked.Increment(ref consensusSyncedAny);
                break; // Synced successfully from this peer
            }
            catch (Exception ex)
            {
                _logger.LogWarning("[P2P Mesh Management] Progressive sync failed targeting node {Id}: {Msg}. Iterating to fallback.", target.NodeId, ex.Message);
                cts.Cancel();
                await _mergeEngine.AbortMergeOperationAsync(stagingOp);
            }
        }

        if (consensusSyncedAny > 0)
        {
            await _mergeEngine.FinalizeTotalMergeAsync(stagingOp);
            return true;
        }

        await _mergeEngine.AbortMergeOperationAsync(stagingOp);
        return false;
    }

    private async Task<string> RequestConsensusWakeUpSegmented_Handler(string senderNodeId, P2PPacketEnvelope envelope, CancellationToken cancelToken)
    {
        _logger.LogInformation("[Consensus Coordinator] Serving latest finalized consensus segments to peer: {Id}", senderNodeId);

        var latestDayKey = _consensusStore.GetAvailableHistoricalDays()
            .OrderByDescending(d => d)
            .FirstOrDefault();

        if (string.IsNullOrEmpty(latestDayKey))
        {
            await _p2pRouter.SendProgressiveResponseAsync(senderNodeId, envelope.MessageId, envelope.CommandType, "{}", 100, true, "NO_DATA");
            return string.Empty;
        }

        var nodeIds = _consensusStore.GetHistoricalPeers(latestDayKey);
        if (nodeIds == null || nodeIds.Count == 0)
        {
            await _p2pRouter.SendProgressiveResponseAsync(senderNodeId, envelope.MessageId, envelope.CommandType, "{}", 100, true, "NO_DATA");
            return string.Empty;
        }

        int totalNodesCount = nodeIds.Count;
        int batchSize = 40;
        int totalChunks = (int)Math.Ceiling((double)totalNodesCount / batchSize);

        for (int i = 0; i < totalChunks; i++)
        {
            var chunkIds = nodeIds.Skip(i * batchSize).Take(batchSize).ToList();
            var nodesInChunk = new List<PeerNodeTransferred>();

            foreach (var id in chunkIds)
            {
                var peerHistoric = _consensusStore.GetHistoricalPeer(latestDayKey, id);
                if (peerHistoric != null)
                {
                    nodesInChunk.Add(new PeerNodeTransferred
                    {
                        Identity = peerHistoric.Identity,
                        TrustScore = peerHistoric.TrustScore,
                        LastSeen = peerHistoric.LastSeen
                    });
                }
            }

            var docSegment = new NodesTransferContainer
            {
                Nodes = nodesInChunk
            };

            string chunkSerialized = JsonSerializer.Serialize(docSegment);
            double progressPercent = (double)(i + 1) / totalChunks * 100.0;
            bool isCompleted = (i == totalChunks - 1);

            await _p2pRouter.SendProgressiveResponseAsync(
                senderNodeId,
                envelope.MessageId,
                envelope.CommandType,
                chunkSerialized,
                progressPercent,
                isCompleted);

            await Task.Delay(30, cancelToken);
        }

        return string.Empty;
    }

    public async Task PingNodesAsync()
    {
        var activeConnections = _connectionManager.GetActiveConnections().ToList();
        foreach (var conn in activeConnections)
        {
            if (conn != null && conn.PeerConnection != null && conn.PeerConnection.connectionState == SIPSorcery.Net.RTCPeerConnectionState.connected)
                await _p2pRouter.SendMessage(conn.NodeId, "Ping", "{}", new RouteSettings { Timeout = TimeSpan.FromSeconds(4) });
        }
    }

    public async Task<bool> SendHeartbeatAsync(string nodeId)
    {
        var activeConn = _connectionManager.GetConnection(nodeId);
        if (activeConn != null && activeConn.PeerConnection != null)
        {
            if (activeConn.PeerConnection.connectionState == SIPSorcery.Net.RTCPeerConnectionState.connected)
            {
                var result = await _p2pRouter.SendRequestAsync(nodeId, "Ping", "{}", new RouteSettings { Timeout = TimeSpan.FromSeconds(5) });
                if (result.IsSuccess)
                {
                    await _peerTracker.UpdatePeerActivityAsync(nodeId);
                    return true;
                }
            }
        }

        return false;
    }

    public async Task<bool> CheckEndpointsALiveAsync(string nodeId)
    {
        var targetPeer = _peerTracker.GetPeer(nodeId);
        if (targetPeer == null) return false;

        List<string> activeEndpoints = new();
        foreach (var endpoint in targetPeer.Identity.SignalingEndpoints)
        {
            if (await IsEndpointActiveAsync(endpoint))
            {
                activeEndpoints.Add(endpoint);
            }
        }

        var target = activeEndpoints.FirstOrDefault();
        if (!string.IsNullOrEmpty(target))
        {
            await _peerTracker.UpdatePeerActivityAsync(nodeId);
            return true;
        }

        return false;
    }

    private async Task<bool> IsEndpointActiveAsync(string endpoint)
    {
        try
        {
            string pingUrl = $"{endpoint.TrimEnd('/')}/p2p/ping";
            var response = await _http.GetAsync(pingUrl);
            return response != null && response.IsSuccessStatusCode && await response.Content.ReadAsStringAsync() == "PONG";
        }
        catch (Exception ex)
        {
            _logger.LogDebug("[P2P Transport] Ping check failed for {Endpoint}: {Msg}", endpoint, ex.Message);
            return false;
        }
    }

    public override void Dispose()
    {
        _heartbeatTimer?.Dispose();
        _settingsChangeListener?.Dispose();

        _p2pRouter.UnregisterCommandHandler("Ping");
        _p2pRouter.UnregisterCommandHandler("RequestConsensusWakeUpSegmented");


        base.Dispose();
    }
}