using System.Text.Json;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using FileTransferModule.Models;

using P2PModule.Configuration;
using P2PModule.Interfaces;
using P2PModule.Models;
using P2PModule.Services.Connection;
using P2PModule.Services.Persistence;

namespace FileTransferModule.Services;

/// <summary>
/// Sends TrackerAnnounce outbound. Before this class, FileTrackerCoordinator's TrackerAnnounce
/// handler only ever received announces - nothing in the codebase ever called it, so a seeder was
/// only discoverable via a manifest's baked-in InitialPeers or the (separate) file-catalog
/// consensus channel, never via the tracker/swarm-registry mechanism itself.
///
/// Role model (gossiped via P2PSettings.NodeRoles, same opt-in mechanism as everything else):
///  - "file_tracker" = this node runs the tracker/directory service (FileTrackerCoordinator answers
///    TrackerAnnounce/TrackerGetPeers/etc) and also participates in file exchange.
///  - "file_node" = this node participates in file exchange (can seed/download) without
///    necessarily running the tracker service itself.
/// Outbound announcing only happens when THIS node advertises "file_node" - no announces are ever
/// sent otherwise, matching the act-solely-on-request rule (nothing happens until the admin opts
/// this node into file_node via settings).
///
/// Two triggers, both event-driven (no polling/timers):
///  - IFileArchiveService.OnFileIngested: a new local file is ready - announce it to every
///    currently known "file_tracker" peer.
///  - IPeerTracker.OnPeerNew/OnPeerUpdate: a peer's gossiped identity reveals "file_tracker" for
///    the first time - send it a catch-up announce of everything we currently host. Deduped per
///    peer so this doesn't refire on every unrelated identity gossip refresh.
/// </summary>
public interface IFileTrackerAnnounceService
{
    /// <summary>
    /// Manually announces every currently-tracked non-hidden local file to every currently known
    /// "file_tracker" peer, right now - for a UI "announce now" button, independent of the
    /// periodic 10-minute loop and the event-driven triggers. Still gated on this node advertising
    /// "file_node" in P2PSettings.NodeRoles; returns 0 without announcing anything if not.
    /// Returns the number of (file, tracker) announce attempts made.
    /// </summary>
    Task<int> TriggerAnnounceAllAsync();

    /// <summary>Whether this node currently advertises "file_node" in P2PSettings.NodeRoles - i.e.
    /// whether any announcing (event-driven, periodic, or manual) will actually do anything.</summary>
    bool IsFileNodeActive();

    /// <summary>Ids of currently known peers advertising the "file_tracker" role.</summary>
    List<string> GetKnownTrackerPeerIds();
}

public class FileTrackerAnnounceService : IFileTrackerAnnounceService, IHostedService
{
    private readonly IPeerTracker _peerTracker;
    private readonly IFileArchiveService _archiveService;
    private readonly IP2PRouterService _routerService;
    private readonly INodeIdentityService _identity;
    private readonly IOptionsMonitor<P2PSettings> _p2pSettings;
    private readonly ILogger<FileTrackerAnnounceService> _logger;

    private readonly HashSet<string> _caughtUpTrackerNodeIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _caughtUpLock = new();
    private static readonly TimeSpan ReannounceInterval = TimeSpan.FromMinutes(10); // safely under FileTrackerCoordinator's 15-min TTL
    private CancellationTokenSource? _periodicCts;
    private Task? _periodicTask;

    public FileTrackerAnnounceService(
        IPeerTracker peerTracker,
        IFileArchiveService archiveService,
        IP2PRouterService routerService,
        INodeIdentityService identity,
        IOptionsMonitor<P2PSettings> p2pSettings,
        ILogger<FileTrackerAnnounceService> logger)
    {
        _peerTracker = peerTracker;
        _archiveService = archiveService;
        _routerService = routerService;
        _identity = identity;
        _p2pSettings = p2pSettings;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _archiveService.OnFileIngested += OnFileIngested;
        _peerTracker.OnPeerNew += OnPeerDiscoveredOrUpdated;
        _peerTracker.OnPeerUpdate += OnPeerDiscoveredOrUpdated;

        // Proactive re-announce so a live seeder never actually goes stale in a tracker's registry -
        // the 15-min TTL in FileTrackerCoordinator is a dead-node cleanup safety net, not a
        // deliberate cutoff for nodes that are still around. Only announces while file_node is
        // advertised; if that's turned off, this loop simply stops sending anything (checked fresh
        // every cycle) rather than needing to be told to stop.
        _periodicCts = new CancellationTokenSource();
        _periodicTask = Task.Run(() => RunPeriodicReannounceLoopAsync(_periodicCts.Token), CancellationToken.None);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _archiveService.OnFileIngested -= OnFileIngested;
        _peerTracker.OnPeerNew -= OnPeerDiscoveredOrUpdated;
        _peerTracker.OnPeerUpdate -= OnPeerDiscoveredOrUpdated;

        _periodicCts?.Cancel();
        return Task.CompletedTask;
    }

    private async Task RunPeriodicReannounceLoopAsync(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(ReannounceInterval);
            while (await timer.WaitForNextTickAsync(ct))
            {
                if (!WeAdvertiseFileNode()) continue; // opted out - stay silent this cycle

                var localFiles = await _archiveService.GetTrackedFilesAsync(includeHidden: false);
                if (localFiles.Count == 0) continue;

                var trackerPeerIds = GetKnownTrackerPeerIds();
                if (trackerPeerIds.Count == 0) continue;

                _logger.LogInformation("[FileTrackerAnnounce] Periodic re-announce: {FileCount} file(s) to {TrackerCount} known file_tracker peer(s).",
                    localFiles.Count, trackerPeerIds.Count);

                foreach (var trackerNodeId in trackerPeerIds)
                {
                    foreach (var f in localFiles)
                    {
                        if (ct.IsCancellationRequested) return;
                        if (!string.IsNullOrEmpty(f.Manifest?.ContentHash))
                        {
                            await AnnounceFileAsync(trackerNodeId, f.Manifest.ContentHash);
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
    }

    public bool IsFileNodeActive() => WeAdvertiseFileNode();

    public async Task<int> TriggerAnnounceAllAsync()
    {
        if (!WeAdvertiseFileNode())
        {
            _logger.LogInformation("[FileTrackerAnnounce] Manual announce-all requested but this node does not advertise 'file_node'. Nothing sent.");
            return 0;
        }

        var localFiles = await _archiveService.GetTrackedFilesAsync(includeHidden: false);
        var trackerPeerIds = GetKnownTrackerPeerIds();

        if (localFiles.Count == 0 || trackerPeerIds.Count == 0)
        {
            _logger.LogInformation("[FileTrackerAnnounce] Manual announce-all: {FileCount} local file(s), {TrackerCount} known tracker(s) - nothing to do.",
                localFiles.Count, trackerPeerIds.Count);
            return 0;
        }

        int attempts = 0;
        foreach (var trackerNodeId in trackerPeerIds)
        {
            foreach (var f in localFiles)
            {
                if (string.IsNullOrEmpty(f.Manifest?.ContentHash)) continue;
                await AnnounceFileAsync(trackerNodeId, f.Manifest.ContentHash);
                attempts++;
            }
        }

        _logger.LogInformation("[FileTrackerAnnounce] Manual announce-all complete: {Attempts} announce(s) sent ({FileCount} file(s) x {TrackerCount} tracker(s)).",
            attempts, localFiles.Count, trackerPeerIds.Count);

        return attempts;
    }

    private bool WeAdvertiseFileNode()
    {
        var roles = _p2pSettings.CurrentValue.NodeRoles;
        return roles != null && roles.Contains(FileTransferModuleEntryPoint.FileNode, StringComparer.OrdinalIgnoreCase);
    }

    private void OnFileIngested(FileArchiveItemRecord record)
    {
        if (!WeAdvertiseFileNode())
        {
            _logger.LogInformation("[FileTrackerAnnounce] Not announcing {Hash} - this node does not advertise 'file_node' (add it to P2PSettings.NodeRoles to enable).", record.Manifest?.ContentHash);
            return;
        }

        if (string.IsNullOrEmpty(record.Manifest?.ContentHash)) return;

        var trackerPeerIds = GetKnownTrackerPeerIds();
        if (trackerPeerIds.Count == 0)
        {
            _logger.LogInformation("[FileTrackerAnnounce] No known 'file_tracker' peers to announce {Hash} to yet.", record.Manifest.ContentHash);
            return;
        }

        foreach (var trackerNodeId in trackerPeerIds)
        {
            _ = AnnounceFileAsync(trackerNodeId, record.Manifest.ContentHash);
        }
    }

    private void OnPeerDiscoveredOrUpdated(PeerNodeRuntime peer)
    {
        var peerNodeId = peer?.Identity?.NodeId;
        if (string.IsNullOrEmpty(peerNodeId) || peerNodeId.Equals(_identity.NodeId, StringComparison.OrdinalIgnoreCase))
            return;

        if (!WeAdvertiseFileNode()) return;

        var theirRoles = peer!.Identity.Roles;
        if (theirRoles == null || !theirRoles.Contains(FileTransferModuleEntryPoint.FileTracker, StringComparer.OrdinalIgnoreCase))
            return;

        lock (_caughtUpLock)
        {
            if (!_caughtUpTrackerNodeIds.Add(peerNodeId)) return; // already caught up with this tracker
        }

        _ = CatchUpAnnounceAsync(peerNodeId);
    }

    private async Task CatchUpAnnounceAsync(string trackerNodeId)
    {
        var localFiles = await _archiveService.GetTrackedFilesAsync(includeHidden: false);
        if (localFiles.Count == 0)
        {
            _logger.LogInformation("[FileTrackerAnnounce] Discovered file_tracker {TrackerNodeId} but have nothing to announce yet.", trackerNodeId);
            return;
        }

        _logger.LogInformation("[FileTrackerAnnounce] Discovered file_tracker {TrackerNodeId} - sending catch-up announce for {Count} hosted file(s).", trackerNodeId, localFiles.Count);

        foreach (var f in localFiles)
        {
            if (!string.IsNullOrEmpty(f.Manifest?.ContentHash))
            {
                await AnnounceFileAsync(trackerNodeId, f.Manifest.ContentHash);
            }
        }
    }

    public List<string> GetKnownTrackerPeerIds()
    {
        return _peerTracker.GetLastSeenNodes(PeerTracker.DefaultNodeComparer)
            .Where(p => p.Identity?.NodeId != null
                        && !p.Identity.NodeId.Equals(_identity.NodeId, StringComparison.OrdinalIgnoreCase)
                        && p.Identity.Roles != null
                        && p.Identity.Roles.Contains(FileTransferModuleEntryPoint.FileTracker, StringComparer.OrdinalIgnoreCase))
            .Select(p => p.Identity!.NodeId)
            .ToList();
    }

    private async Task AnnounceFileAsync(string trackerNodeId, string contentHash)
    {
        try
        {
            var payload = new TrackerAnnouncePayload
            {
                ContentHash = contentHash,
                NodeId = _identity.NodeId,
                SignalingEndpoints = _identity.GetEndpoints()
            };

            var result = await _routerService.SendRequestAsync(
                trackerNodeId, "TrackerAnnounce", JsonSerializer.Serialize(payload),
                new RouteSettings { Timeout = TimeSpan.FromSeconds(15) });

            if (result.IsSuccess)
            {
                _logger.LogInformation("[FileTrackerAnnounce] Announced {Hash} to file_tracker {TrackerNodeId}.", contentHash, trackerNodeId);
            }
            else
            {
                _logger.LogWarning("[FileTrackerAnnounce] Announce of {Hash} to {TrackerNodeId} failed: {Reason}", contentHash, trackerNodeId, result.Payload);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[FileTrackerAnnounce] Error announcing {Hash} to {TrackerNodeId}.", contentHash, trackerNodeId);
        }
    }
}
