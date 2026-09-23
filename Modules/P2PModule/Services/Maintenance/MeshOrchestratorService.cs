// [full-implementation]
using System;
using System.Linq;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using P2PModule.Configuration;
using P2PModule.Infrastructure;
using P2PModule.Interfaces;
using P2PModule.Models;
using P2PModule.Services.Connection;
using P2PModule.Services.Discovery;
using P2PModule.Services.Persistence;
using P2PModule.Services.Rating;

namespace P2PModule.Services.Maintenance;

/// <summary>
/// Background worker that maintains the partial-mesh topology.
/// Ensures connection to trackers and promoted nodes to prevent graph severance.
/// Bridges low-latency topology enforcement with deep-scheduled mesh maintenance.
/// </summary>
public class MeshOrchestratorService : BackgroundService
{
    private readonly ILogger<MeshOrchestratorService> _logger;
    private readonly IOptionsMonitor<P2PSettings> _generalSettings;
    private readonly IMeshManagementService _management;
    private readonly IPeerConnectionManager _connectionManager;
    private readonly IPeerTracker _peerService;
    private readonly IConsensusStore _store;
    private readonly IRatingService _ratingService;

    private DateTime _lastDeepMaintenanceTime = DateTime.MinValue;
    private readonly TimeSpan _deepMaintenanceInterval = TimeSpan.FromMinutes(5);

    public MeshOrchestratorService(
        ILogger<MeshOrchestratorService> logger,
        IOptionsMonitor<P2PSettings> settings,
        IMeshManagementService management,
        IPeerConnectionManager connectionManager,
        IPeerTracker peerService,
        IConsensusStore store,
        IRatingService ratingService)
    {
        _store = store;
        _peerService = peerService;
        _management = management;
        _connectionManager = connectionManager;
        _ratingService = ratingService;
        _generalSettings = settings;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[P2P Mesh] Orchestrator waiting {Sec}s for system stabilization...", _generalSettings.CurrentValue.StartupDelaySeconds);
        
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(_generalSettings.CurrentValue.StartupDelaySeconds), stoppingToken);

            await _peerService.InitializeAsync();
            // Parallel High-Frequency Ping loop task to prevent tunnel dropping and keep nat mappings open
            _ = StartPeriodicPingLoopAsync(stoppingToken);

            // Synchronize our maintenance tracking baseline to current clock right after warmup drops out
            //_lastDeepMaintenanceTime = DateTime.UtcNow;
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("[P2P Mesh] Orchestrator execution canceled during initialization warmup.");
            return;
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "[P2P Mesh] Fatal error during cold boot initialization.");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var now = DateTime.UtcNow;

            try
            {
                // 1. Run low-latency watchdog tasks (Relay/Reputation Pruning)
                await EnforceTopologyWatchdogAsync();

                // 2. Run heavier scheduled maintenance tasks periodically (every 5 minutes)
                if (now - _lastDeepMaintenanceTime >= _deepMaintenanceInterval)
                {
                    await PerformDeepMeshMaintenanceAsync(stoppingToken);
                    _lastDeepMaintenanceTime = now;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[P2P Mesh] Exception caught during main background maintenance tick.");
            }

            // Run orchestrator loop on heartbeat intervals (typically 30 seconds)
            var heartbeatInterval = _generalSettings.CurrentValue.TopologyMaintenannceIntervalMs > 0
                ? _generalSettings.CurrentValue.TopologyMaintenannceIntervalMs
                : 30000;

            await Task.Delay(TimeSpan.FromMilliseconds(heartbeatInterval), stoppingToken);
        }
    }

    /// <summary>
    /// High frequency ping loop that keeps connections alive and healthy.
    /// </summary>
    private async Task StartPeriodicPingLoopAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _management.PingNodesAsync();
            }
            catch (Exception ex)
            {
                _logger.LogTrace(ex, "[P2P Mesh] Keep-alive ping iteration skipped or failed.");
            }

            // Ping every 15 seconds to stay ahead of NAT/tunnel drop limits and idle watchdogs
            await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
        }
    }

    /// <summary>
    /// Checks active connection states, drops connections with bankrupt trust scores,
    /// and prunes inactive WebRTC relay paths.
    /// </summary>
    private async Task EnforceTopologyWatchdogAsync()
    {
        var activeConnections = _connectionManager.GetActiveConnections().ToList();
        var now = DateTime.UtcNow;

        var maxTemporaryInactivity = _generalSettings.CurrentValue.MaxRelayInactivitySeconds > 0
            ? _generalSettings.CurrentValue.MaxRelayInactivitySeconds
            : 45;

        var maxDirectInactivity = _generalSettings.CurrentValue.ActiveNodeSecondsBackupPeriod > 0
            ? _generalSettings.CurrentValue.ActiveNodeSecondsBackupPeriod
            : 300; // default to 5 minutes of total silence before kick

        foreach (var conn in activeConnections)
        {
            var peer = _peerService.GetPeer(conn.NodeId);

            // Reputation-based connection drop guard
            if (peer != null && !_ratingService.IsConnectivityValid(peer.Identity.NodeId, applyOverrides: true))
            {
                await _connectionManager.KillConnectionAsync(conn.NodeId, "Reputation bankruptcy / block");
                continue;
            }

            // WebRTC Temporary connections Pruning
            // temporary connectiosn are conenctions that have been estabilished but not yet completed handshake
            if (conn.ConnectionTypes.Contains("temporary"))
            {
                if ((now - conn.LastActivity).TotalSeconds >= maxTemporaryInactivity)
                {
                    _logger.LogWarning("[P2P Mesh] Relay timeout hit for {NodeId}. Dropping 'relay' role.", conn.NodeId);

                    // First, drop the "relay" role
                    conn.ConnectionTypes.Remove("temporary");

                    // Only then check whether it was promoted (has any other roles remaining, e.g., outbound/inbound).
                    // If no other roles, drop the connection.
                    if (!conn.ConnectionTypes.Any())
                    {
                        await _connectionManager.KillConnectionAsync(conn.NodeId, "Temporary relay tunnel inactivity timeout");
                    }
                }
            }

            // attempt reconnections
            if(conn.ConnectionTypes.Contains("outbound"))
            {
                var isalive = await _management.SendHeartbeatAsync(conn.NodeId);
                if (!isalive)
                {
                    _logger.LogWarning("[P2P Mesh Management] Link heartbeat failed for {NodeId}. Re-dialing...", conn.NodeId);

                    ActivePeerConnection? reconnected = null;
                    if (peer != null)
                    {
                        await _connectionManager.ConnectPeerAsync(peer.Identity.NodeId, peer.Identity.SignalingEndpoints.ToArray(), "temporary");
                    }
                }
                await _management.CheckEndpointsALiveAsync(conn.NodeId);
            }

            // Direct active connection inactivity watchdog
            if (conn.ConnectionTypes.Contains("outbound") || conn.ConnectionTypes.Contains("inbound"))
            {
                if ((now - conn.LastActivity).TotalSeconds >= maxDirectInactivity)
                {
                    //no pings whatsoever
                    _logger.LogWarning("[P2P Mesh] Direct connection inactive for {Sec}s. Terminating connection with {NodeId}.",
                        (now - conn.LastActivity).TotalSeconds, conn.NodeId);

                    await _connectionManager.KillConnectionAsync(conn.NodeId, "Direct connection idle inactivity watchdog timeout");

                }
            }
        }
    }

    /// <summary>
    /// Executes fine-grained scheduled peer discovery, partition rotation, and dirty memory sync writebacks.
    /// </summary>
    private async Task PerformDeepMeshMaintenanceAsync(CancellationToken stoppingToken)
    {
        _logger.LogDebug("[P2P Mesh] Running scheduled deep topology maintenance...");

        var activeNodes = _peerService.GetLastSeenNodes(PeerTracker.DefaultNodeComparer).ToList();

        // Check if our tracking databases are totally blind
        if (!activeNodes.Any())
        {
            _logger.LogInformation("[P2P Mesh] No known active peers. Triggering Seed Tracker discovery pass...");

            // Bootstrap execution occurs right here!
            await BootstrapFromSeedTrackersAsync(stoppingToken);

            // Re-evaluate known peers cache following bootstrap injection pass
            activeNodes = _peerService.GetLastSeenNodes(PeerTracker.DefaultNodeComparer).ToList();
        }

        // 1. Maintain active outbound connections to satisfy mesh targets
        if (!activeNodes.Any())
        {
            _logger.LogInformation("[P2P Mesh] No known active peers. Performing initial discovery handshakes.");
            await _management.PerformInitialHandshakeAsync();
        }
        else
        {
            // Execute dynamic liveness audits and connection maintenance loops
            await _management.MaintainOutboundConnectionsAsync();
        }

        // 2. Check if the current daily consensus partition is outdated (Calendar date drift)
        if (_store.IsOutdated())
        {
            _logger.LogInformation("[P2P Mesh] Today's daily consensus partition is outdated. Swapping to current sharded index...");
            await _management.DiscoverNewConsensus();
        }

        // 3. Deferred Writeback: Periodically persist pending dirty memory updates to sharded storage
        _logger.LogDebug("[P2P Mesh] Persisting deferred memory index structures to disk.");
        await _peerService.FlushPendingUpdatesToDiskAsync();
    }


    private async Task BootstrapFromSeedTrackersAsync(CancellationToken ct)
    {
        var currentConfig = _generalSettings.CurrentValue;
        if (currentConfig.SeedEndpoints == null || !currentConfig.SeedEndpoints.Any())
        {
            _logger.LogWarning("[P2P Bootstrap] No initial SeedTrackers defined in settings.");
            return;
        }

        // Grab our current known nodes to verify address overlap prior to making network queries
        var existingPeers = _peerService.GetLastSeenNodes(PeerTracker.DefaultNodeComparer).ToList();

        using var client = new HttpClient();
        client.Timeout = TimeSpan.FromSeconds(currentConfig.HttpTimeoutSeconds);

        foreach (var trackerEndpointUrl in currentConfig.SeedEndpoints)
        {
            if (string.IsNullOrWhiteSpace(trackerEndpointUrl)) continue;

            string normalizedBase = trackerEndpointUrl.TrimEnd('/');

            // DEDUPLICATION CHECK 1: Skip if we are already tracking this specific base URL string
            bool isAlreadyRegistered = existingPeers.Any(p => p.Identity.SignalingEndpoints.Contains(trackerEndpointUrl));

            if (isAlreadyRegistered)
            {
                _logger.LogDebug("[P2P Bootstrap] Skipping tracker URL probe: {Url} is already an active peer.", normalizedBase);
                continue;
            }

            string discoveryUrl = $"{normalizedBase}/p2p/identity";
            _logger.LogInformation("[P2P Bootstrap] Probing new seed tracker entry point: {Url}", discoveryUrl);

            try
            {
                var response = await client.GetAsync(discoveryUrl, ct);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("[P2P Bootstrap] Seed tracker {Url} responded with error status: {Status}",
                        discoveryUrl, response.StatusCode);
                    continue;
                }

                var identityData = await response.Content.ReadFromJsonAsync<PeerImmutableData>(cancellationToken: ct);
                if (identityData == null || string.IsNullOrEmpty(identityData.NodeId))
                {
                    _logger.LogWarning("[P2P Bootstrap] Received empty or corrupted identity payload from {Url}", discoveryUrl);
                    continue;
                }

                // Handoff to our safe trust validation and ingestion routine
                await IngestIdentitySafelyAsync(identityData, normalizedBase);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[P2P Bootstrap] Failed communicating with seed endpoint line: {Url}", discoveryUrl);
            }
        }
    }
    private async Task IngestIdentitySafelyAsync(PeerImmutableData incomingData, string sourceUrl)
    {
        if (incomingData == null || string.IsNullOrEmpty(incomingData.NodeId)) return;

        // Target current active epoch (historic: false) to prevent overwriting past finalized consensus records
        var existingPeer = _peerService.GetPeer(incomingData.NodeId, historic: false);

        if (existingPeer == null)
        {
            // Cryptographic Guard: Verify signature attached to identity metadata packet
            // Assuming incomingData exposes a verification mechanism or signature payload
            if (incomingData.Signature == null ||
                !P2PCrypto.TestSignature(
                    incomingData.GetNodeDataHash(),
                    incomingData.PublicVerifyKey,
                    incomingData.Signature))
            {
                _logger.LogWarning("[P2P Security] Cryptographic signature invalid for Node: {NodeId} via {Url}. Dropping payload.",
                    incomingData.NodeId, sourceUrl);
                return;
            }


            _logger.LogInformation("[P2P Bootstrap] Adding unmapped node profile discovered via seed: {NodeId}", incomingData.NodeId);

            // Maps the immutable structure straight to your today partition
            await _peerService.UpdatePeerAsync(incomingData);
        }
        else
        {
            _logger.LogDebug("[P2P Bootstrap] NodeId {NodeId} already exists in today's tracking registry. Skipping.", incomingData.NodeId);
        }
    }

}