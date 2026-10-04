
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using FileTransferModule.Configuration;
using FileTransferModule.Models.Torrent;

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
    Task RegisterAnnounceAsync(string contentHash, TorrentPeerDescriptor peer);

    /// <summary>
    /// Removes a peer from the active seeders list (e.g., on disconnect).
    /// </summary>
    Task UnregisterPeerAsync(string contentHash, string nodeId);

    /// <summary>
    /// Retrieves the list of active peers holding the specified file.
    /// </summary>
    Task<List<TorrentPeerDescriptor>> GetPeersForFileAsync(string contentHash);

    /// <summary>
    /// Stores / registers a new .ptorrent manifest on this tracker node.
    /// </summary>
    Task RegisterManifestAsync(PTorrentManifest manifest);

    /// <summary>
    /// Retrieves a manifest by its ContentHash if hosted on this tracker.
    /// </summary>
    Task<PTorrentManifest?> GetManifestAsync(string contentHash);

    /// <summary>
    /// Returns all manifests tracked by this node.
    /// </summary>
    Task<List<PTorrentManifest>> GetAllTrackedManifestsAsync(bool includeHidden = false);
}

public class TrackerAnnouncePayload
{
    public string ContentHash { get; set; } = string.Empty;
    public string NodeId { get; set; } = string.Empty;
    public List<string> SignalingEndpoints { get; set; } = new();
}

public class SwarmRegistrationRecord
{
    public TorrentPeerDescriptor Peer { get; set; } = new();
    public DateTime LastAnnouncedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Coordinator for the "file_tracker" role. Manages in-memory and local index mapping
/// of ContentHash -> Peers and responds to network tracker commands over IP2PRouterService.
/// </summary>
public class FileTrackerCoordinator : IFileTrackerCoordinator, IDisposable
{
    private readonly IP2PRouterService _router;
    private readonly IFileArchiveService _archiveService;
    private readonly ILogger<FileTrackerCoordinator> _logger;

    // ContentHash -> (NodeId -> RegistrationRecord)
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, SwarmRegistrationRecord>> _swarmRegistry = new(StringComparer.OrdinalIgnoreCase);

    // Standalone registry for remote manifests stored on tracker nodes
    private readonly ConcurrentDictionary<string, PTorrentManifest> _remoteManifestStore = new(StringComparer.OrdinalIgnoreCase);

    public FileTrackerCoordinator(
        IP2PRouterService router,
        IFileArchiveService archiveService,
        ILogger<FileTrackerCoordinator> logger)
    {
        _router = router;
        _archiveService = archiveService;
        _logger = logger;

        RegisterTrackerHandlers();
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

                await RegisterAnnounceAsync(payload.ContentHash, peer);
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

    public Task RegisterAnnounceAsync(string contentHash, TorrentPeerDescriptor peer)
    {
        if (string.IsNullOrWhiteSpace(contentHash) || peer == null || string.IsNullOrWhiteSpace(peer.NodeId))
        {
            return Task.CompletedTask;
        }

        var swarms = _swarmRegistry.GetOrAdd(contentHash, _ => new ConcurrentDictionary<string, SwarmRegistrationRecord>(StringComparer.OrdinalIgnoreCase));
        swarms[peer.NodeId] = new SwarmRegistrationRecord
        {
            Peer = peer,
            LastAnnouncedUtc = DateTime.UtcNow
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
        var cutoff = DateTime.UtcNow.AddMinutes(-15);

        // 1. Gather active in-memory announced peers
        if (_swarmRegistry.TryGetValue(contentHash, out var swarmDict))
        {
            peers.AddRange(
                swarmDict.Values
                    .Where(r => r.LastAnnouncedUtc >= cutoff)
                    .Select(r => r.Peer)
            );
        }

        // 2. If locally hosted/seeded, ensure local node initial peers are included
        var localFile = await _archiveService.GetTrackedFileAsync(contentHash);
        if (localFile != null && localFile.Manifest != null)
        {
            foreach (var initialSeeder in localFile.Manifest.InitialPeers)
            {
                if (!peers.Any(p => string.Equals(p.NodeId, initialSeeder.NodeId, StringComparison.OrdinalIgnoreCase)))
                {
                    peers.Add(initialSeeder);
                }
            }
        }

        return peers;
    }

    public Task RegisterManifestAsync(PTorrentManifest manifest)
    {
        if (manifest != null && !string.IsNullOrWhiteSpace(manifest.ContentHash))
        {
            _remoteManifestStore[manifest.ContentHash] = manifest;
        }

        return Task.CompletedTask;
    }

    public async Task<PTorrentManifest?> GetManifestAsync(string contentHash)
    {
        if (string.IsNullOrWhiteSpace(contentHash)) return null;

        // 1. Check local archive service
        var localRecord = await _archiveService.GetTrackedFileAsync(contentHash);
        if (localRecord != null)
        {
            return localRecord.Manifest;
        }

        // 2. Check remote manifest memory store
        if (_remoteManifestStore.TryGetValue(contentHash, out var manifest))
        {
            return manifest;
        }

        return null;
    }

    public async Task<List<PTorrentManifest>> GetAllTrackedManifestsAsync(bool includeHidden = false)
    {
        var result = new Dictionary<string, PTorrentManifest>(StringComparer.OrdinalIgnoreCase);

        // 1. Local archive files filtered by the includeHidden parameter
        var localFiles = await _archiveService.GetTrackedFilesAsync(includeHidden);
        foreach (var file in localFiles)
        {
            result[file.Manifest.ContentHash] = file.Manifest;
        }

        // 2. Add remote tracked manifests
        foreach (var kvp in _remoteManifestStore)
        {
            if (!result.ContainsKey(kvp.Key))
            {
                result[kvp.Key] = kvp.Value;
            }
        }

        return result.Values.ToList();
    }

    #endregion

    public void Dispose()
    {
        _router.UnregisterCommandHandler("TrackerAnnounce");
        _router.UnregisterCommandHandler("TrackerGetPeers");
        _router.UnregisterCommandHandler("TrackerGetManifest");
        _router.UnregisterCommandHandler("TrackerGetAllManifests");
        _swarmRegistry.Clear();
        _remoteManifestStore.Clear();
    }
}