
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using FileTransferModule.Configuration;
using FileTransferModule.Models.Torrent;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using P2PModule.Interfaces;
using P2PModule.Services.Connection;
using P2PModule.Services.Persistence;
using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using FileTransferModule.Configuration;
using FileTransferModule.Models;
using P2PModule.Interfaces;
using P2PModule.Models.Network;

namespace FileTransferModule.Services;


/// <summary>
/// Coordinator for the "file_tracker" role. Manages local index mapping of ContentHash -> List<TorrentPeerDescriptor>
/// and handles network queries for finding seeders/peers within the swarm.
/// </summary>
public interface IFileTrackerCoordinator
{
    /// <summary>
    /// Registers a peer as a seeder for the specified ContentHash in the tracker table.
    /// </summary>
    /// <param name="validFor">How long the entry stays valid; null = derived from this node's TrackerReannounceIntervalMinutes.</param>
    Task RegisterAnnounceAsync(string contentHash, TorrentPeerDescriptor peer, TimeSpan? validFor = null);

    /// <summary>
    /// Removes a peer from the active seeders list (e.g., on disconnect).
    /// </summary>
    Task UnregisterPeerAsync(string contentHash, string nodeId);

    /// <summary>
    /// Retrieves the list of active peers holding the specified file.
    /// </summary>
    Task<List<TorrentPeerDescriptor>> GetPeersForFileAsync(string contentHash);

    /// <summary>
    /// Retrieves a manifest by its ContentHash if this node hosts the file (archive-backed).
    /// </summary>
    Task<PTorrentManifest?> GetManifestAsync(string contentHash);

    /// <summary>
    /// Returns the manifests of all files this node hosts (archive-backed).
    /// </summary>
    Task<List<PTorrentManifest>> GetAllTrackedManifestsAsync(bool includeHidden = false);
}

public class TrackerAnnouncePayload
{
    public string ContentHash { get; set; } = string.Empty;
    public string NodeId { get; set; } = string.Empty;
    public List<string> SignalingEndpoints { get; set; } = new();

    /// <summary>
    /// The announcer's own re-announce interval. The tracker keeps this entry for 1.5x of it.
    /// 0/missing (older announcers) = the tracker uses its own TrackerReannounceIntervalMinutes.
    /// </summary>
    public int ReannounceIntervalSeconds { get; set; }
}

public class SwarmRegistrationRecord
{
    public TorrentPeerDescriptor Peer { get; set; } = new();
    public DateTime LastAnnouncedUtc { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>Single place that turns a re-announce interval into an announce lifetime.</summary>
public static class TrackerAnnounceTiming
{
    private static readonly TimeSpan MinTtl = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan MaxTtl = TimeSpan.FromHours(36);

    /// <summary>1.5x the interval (one missed re-announce tolerated), clamped to [2 min, 36 h].</summary>
    public static TimeSpan TtlFor(TimeSpan reannounceInterval)
    {
        var ttl = TimeSpan.FromTicks((long)(reannounceInterval.Ticks * 1.5));
        if (ttl < MinTtl) return MinTtl;
        if (ttl > MaxTtl) return MaxTtl;
        return ttl;
    }

    public static TimeSpan IntervalFromSettings(FileSharingSettings s) =>
        TimeSpan.FromMinutes(Math.Max(1, s.TrackerReannounceIntervalMinutes));
}

/// <summary>
/// Coordinator for the "file_tracker" role. Manages in-memory and local index mapping
/// of ContentHash -> Peers and responds to network tracker commands over IP2PRouterService.
/// </summary>
public class FileTrackerCoordinator : IFileTrackerCoordinator, IHostedService, IDisposable
{
    private readonly IP2PRouterService _router;
    private readonly IFileArchiveService _archiveService;
    private readonly INodeIdentityService _identity;
    private readonly IOptionsMonitor<FileSharingSettings> _settings;
    private readonly ILogger<FileTrackerCoordinator> _logger;

    // ContentHash -> (NodeId -> RegistrationRecord)
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, SwarmRegistrationRecord>> _swarmRegistry = new(StringComparer.OrdinalIgnoreCase);

    // Announce lifetime is per entry (SwarmRegistrationRecord.ExpiresUtc), derived from the
    // announcer's own re-announce interval - see TrackerAnnounceTiming. A live seeder never
    // expires; a dead one drops out of TrackerGetPeers at ExpiresUtc and the sweep deletes it.

    private CancellationTokenSource? _sweepCts;

    public FileTrackerCoordinator(
        IP2PRouterService router,
        IFileArchiveService archiveService,
        INodeIdentityService identity,
        IOptionsMonitor<FileSharingSettings> settings,
        ILogger<FileTrackerCoordinator> logger)
    {
        _router = router;
        _archiveService = archiveService;
        _identity = identity;
        _settings = settings;
        _logger = logger;
    }

    // Registered as a hosted service (see FileTransferModuleEntryPoint) so the tracker command
    // handlers are live from host start. Previously they were registered in the constructor, which
    // only ran the first time something resolved ISwarmDownloadCoordinator (e.g. a controller hit) -
    // until then a "file_tracker" node silently dropped TrackerAnnounce/TrackerGetPeers requests.
    public Task StartAsync(CancellationToken cancellationToken)
    {
        RegisterTrackerHandlers();
        _logger.LogInformation("[FileTracker] Tracker command handlers registered.");

        _sweepCts = new CancellationTokenSource();
        _ = Task.Run(() => RunSweepLoopAsync(_sweepCts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _sweepCts?.Cancel();
        UnregisterTrackerHandlers();
        return Task.CompletedTask;
    }

    private async Task RunSweepLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                // Sweep at half our own re-announce interval (1-5 min); re-read every cycle so a
                // settings change applies without a restart.
                var half = TrackerAnnounceTiming.IntervalFromSettings(_settings.CurrentValue) / 2;
                var sweepEvery = half < TimeSpan.FromMinutes(1) ? TimeSpan.FromMinutes(1)
                    : half > TimeSpan.FromMinutes(5) ? TimeSpan.FromMinutes(5) : half;
                await Task.Delay(sweepEvery, ct);

                int removed = SweepExpiredAnnounces();
                if (removed > 0)
                {
                    _logger.LogInformation("[FileTracker] Swept {Count} expired announce(s); {Hashes} content hash(es) still tracked.", removed, _swarmRegistry.Count);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // shutdown
        }
    }

    /// <summary>
    /// Physically removes announces past their ExpiresUtc, and content hashes left with no peers.
    /// Before this, expired entries were only filtered out on read and never deleted, so a
    /// long-running tracker's registry grew without bound.
    /// </summary>
    private int SweepExpiredAnnounces()
    {
        var now = DateTime.UtcNow;
        int removed = 0;

        foreach (var (contentHash, peers) in _swarmRegistry)
        {
            foreach (var entry in peers)
            {
                if (entry.Value.ExpiresUtc <= now && peers.TryRemove(entry))
                {
                    removed++;
                }
            }

            if (peers.IsEmpty)
            {
                // Only remove the hash if it is still this (empty) dictionary - a concurrent
                // announce may have just added a peer to it.
                _swarmRegistry.TryRemove(new KeyValuePair<string, ConcurrentDictionary<string, SwarmRegistrationRecord>>(contentHash, peers));
            }
        }

        return removed;
    }

    private void UnregisterTrackerHandlers()
    {
        _router.UnregisterCommandHandler("TrackerAnnounce");
        _router.UnregisterCommandHandler("TrackerGetPeers");
        _router.UnregisterCommandHandler("TrackerGetManifest");
        _router.UnregisterCommandHandler("TrackerGetAllManifests");
    }

    private void RegisterTrackerHandlers()
    {
        // 1. Remote peer announces presence/seeding for a ContentHash
        _router.RegisterCommandHandler("TrackerAnnounce", async (senderId, envelope, token) =>
        {
            if (string.IsNullOrEmpty(envelope.SerializedPayload)) return "{\"success\":false}";

            try
            {
                var payload = JsonSerializer.Deserialize<TrackerAnnouncePayload>(envelope.SerializedPayload);
                if (payload == null || string.IsNullOrWhiteSpace(payload.ContentHash))
                {
                    return "{\"success\":false,\"error\":\"Invalid payload\"}";
                }

                var peer = new TorrentPeerDescriptor
                {
                    NodeId = senderId,
                    SignalingEndpoints = payload.SignalingEndpoints ?? new List<string>()
                };

                // Lifetime follows the ANNOUNCER's interval (older announcers send none -> ours).
                TimeSpan? validFor = payload.ReannounceIntervalSeconds > 0
                    ? TrackerAnnounceTiming.TtlFor(TimeSpan.FromSeconds(payload.ReannounceIntervalSeconds))
                    : null;

                await RegisterAnnounceAsync(payload.ContentHash, peer, validFor);
                return "{\"success\":true}";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[FileTracker] Error in TrackerAnnounce handler from {SenderId}", senderId);
                return "{\"success\":false,\"error\":\"Server error\"}";
            }
        });

        // 2. Remote peer requests seeders/peers for a specific ContentHash
        _router.RegisterCommandHandler("TrackerGetPeers", async (senderId, envelope, token) =>
        {
            if (string.IsNullOrEmpty(envelope.SerializedPayload)) return "[]";

            try
            {
                using var doc = JsonDocument.Parse(envelope.SerializedPayload);
                if (!doc.RootElement.TryGetProperty("contentHash", out var hashElem))
                {
                    return "[]";
                }

                string hash = hashElem.GetString() ?? string.Empty;
                if (string.IsNullOrEmpty(hash)) return "[]";

                var activePeers = await GetPeersForFileAsync(hash);
                return JsonSerializer.Serialize(activePeers);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[FileTracker] Error in TrackerGetPeers handler for {SenderId}", senderId);
                return "[]";
            }
        });

        // 3. Remote peer requests .ptorrent manifest metadata by ContentHash
        _router.RegisterCommandHandler("TrackerGetManifest", async (senderId, envelope, token) =>
        {
            if (string.IsNullOrEmpty(envelope.SerializedPayload)) return string.Empty;

            try
            {
                using var doc = JsonDocument.Parse(envelope.SerializedPayload);
                if (!doc.RootElement.TryGetProperty("contentHash", out var hashElem))
                {
                    return string.Empty;
                }

                string hash = hashElem.GetString() ?? string.Empty;
                if (string.IsNullOrEmpty(hash)) return string.Empty;

                var manifest = await GetManifestAsync(hash);
                return manifest != null ? manifest.SerializeToJson() : string.Empty;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[FileTracker] Error in TrackerGetManifest handler for {SenderId}", senderId);
                return string.Empty;
            }
        });

        // 4. Remote peer queries all public manifests tracked by this node
        _router.RegisterCommandHandler("TrackerGetAllManifests", async (senderId, envelope, token) =>
        {
            try
            {
                // Network discovery strictly excludes hidden files
                var publicManifests = await GetAllTrackedManifestsAsync(includeHidden: false);
                return JsonSerializer.Serialize(publicManifests);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[FileTracker] Error in TrackerGetAllManifests handler for {SenderId}", senderId);
                return "[]";
            }
        });
    }

    #region IFileTrackerCoordinator Implementation

    public Task RegisterAnnounceAsync(string contentHash, TorrentPeerDescriptor peer, TimeSpan? validFor = null)
    {
        if (string.IsNullOrWhiteSpace(contentHash) || peer == null || string.IsNullOrWhiteSpace(peer.NodeId))
        {
            return Task.CompletedTask;
        }

        var swarms = _swarmRegistry.GetOrAdd(contentHash, _ => new ConcurrentDictionary<string, SwarmRegistrationRecord>(StringComparer.OrdinalIgnoreCase));
        var now = DateTime.UtcNow;
        var ttl = validFor ?? TrackerAnnounceTiming.TtlFor(TrackerAnnounceTiming.IntervalFromSettings(_settings.CurrentValue));
        swarms[peer.NodeId] = new SwarmRegistrationRecord
        {
            Peer = peer,
            LastAnnouncedUtc = now,
            ExpiresUtc = now + ttl
        };

        _logger.LogDebug("[FileTracker] Peer {NodeId} registered for ContentHash {Hash}", peer.NodeId, contentHash);
        return Task.CompletedTask;
    }

    public Task UnregisterPeerAsync(string contentHash, string nodeId)
    {
        if (!string.IsNullOrWhiteSpace(contentHash) && !string.IsNullOrWhiteSpace(nodeId))
        {
            if (_swarmRegistry.TryGetValue(contentHash, out var swarms))
            {
                swarms.TryRemove(nodeId, out _);
                if (swarms.IsEmpty)
                {
                    _swarmRegistry.TryRemove(contentHash, out _);
                }
            }
        }

        return Task.CompletedTask;
    }

    public async Task<List<TorrentPeerDescriptor>> GetPeersForFileAsync(string contentHash)
    {
        if (string.IsNullOrWhiteSpace(contentHash)) return new List<TorrentPeerDescriptor>();

        var peers = new List<TorrentPeerDescriptor>();
        var now = DateTime.UtcNow;

        // 1. Gather active (not yet expired) announced peers
        if (_swarmRegistry.TryGetValue(contentHash, out var swarmDict))
        {
            peers.AddRange(
                swarmDict.Values
                    .Where(r => r.ExpiresUtc > now)
                    .Select(r => r.Peer)
            );
        }

        // 2. If this node holds the file locally:
        //    - list THIS node as a seeder only while it actually serves slices (AllowDownload).
        //      Previously the local manifest's InitialPeers were returned unconditionally, so a file
        //      with downloads switched off still sent leechers here to a channel that never answered.
        //    - still pass along the manifest's other InitialPeers (other nodes' original seeders),
        //      since our own AllowDownload says nothing about them.
        var localFile = await _archiveService.GetTrackedFileAsync(contentHash);
        if (localFile != null && localFile.Manifest != null)
        {
            string selfId = _identity.NodeId;

            if (localFile.AllowDownload && !string.IsNullOrEmpty(selfId))
            {
                AddIfMissing(peers, new TorrentPeerDescriptor
                {
                    NodeId = selfId,
                    SignalingEndpoints = _identity.GetEndpoints()
                });
            }

            foreach (var initialSeeder in localFile.Manifest.InitialPeers)
            {
                if (string.Equals(initialSeeder.NodeId, selfId, StringComparison.OrdinalIgnoreCase)) continue;
                AddIfMissing(peers, initialSeeder);
            }
        }

        return peers;
    }

    private static void AddIfMissing(List<TorrentPeerDescriptor> peers, TorrentPeerDescriptor candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate.NodeId)) return;
        if (!peers.Any(p => string.Equals(p.NodeId, candidate.NodeId, StringComparison.OrdinalIgnoreCase)))
        {
            peers.Add(candidate);
        }
    }

    public async Task<PTorrentManifest?> GetManifestAsync(string contentHash)
    {
        if (string.IsNullOrWhiteSpace(contentHash)) return null;

        // Hidden files are still returned to a peer that already knows the exact hash - hidden
        // means "not listed", not "not downloadable".
        var localRecord = await _archiveService.GetTrackedFileAsync(contentHash);
        return localRecord?.Manifest;
    }

    public async Task<List<PTorrentManifest>> GetAllTrackedManifestsAsync(bool includeHidden = false)
    {
        var localFiles = await _archiveService.GetTrackedFilesAsync(includeHidden);
        return localFiles
            .Where(f => f.Manifest != null && !string.IsNullOrEmpty(f.Manifest.ContentHash))
            .GroupBy(f => f.Manifest.ContentHash, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First().Manifest)
            .ToList();
    }

    #endregion

    public void Dispose()
    {
        UnregisterTrackerHandlers();
        _sweepCts?.Cancel();
        _swarmRegistry.Clear();
    }
}