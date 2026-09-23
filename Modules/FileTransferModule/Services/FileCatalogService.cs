
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

using FileSharingModule.Models;
using FileSharingModule.Models.Torrent;

using Microsoft.Extensions.Logging;

using P2PModule.Services.Connection;
using P2PModule.Services.Persistence;

namespace FileSharingModule.Services;


/// <summary>
/// Allows browsing and querying available files across the network.
/// Queries advertised "file_tracker" nodes as well as specific ad-hoc nodes directly.
/// </summary>
public interface IFileCatalogService
{
    /// <summary>
    /// Queries all discovered "file_tracker" nodes and local manifests for visible files.
    /// </summary>
    Task<List<FileCatalogEntry>> BrowseNetworkCatalogAsync();

    /// <summary>
    /// Directly probes a specific node ID (even if not an advertised tracker) for its hosted public files.
    /// </summary>
    Task<List<FileCatalogEntry>> QueryNodeCatalogDirectAsync(string nodeId);

    /// <summary>
    /// Fetches the full .ptorrent manifest from a specific host node.
    /// </summary>
    Task<PTorrentManifest?> FetchManifestFromNodeAsync(string hostNodeId, string contentHash);
}

public class FileCatalogService : IFileCatalogService, IDisposable
{
    private readonly IP2PRouterService _router;
    private readonly IPeerTracker _peerTracker;
    private readonly IFileArchiveService _archiveService;
    private readonly ILogger<FileCatalogService> _logger;

    public FileCatalogService(
        IP2PRouterService router,
        IPeerTracker peerTracker,
        IFileArchiveService archiveService,
        ILogger<FileCatalogService> logger)
    {
        _router = router;
        _peerTracker = peerTracker;
        _archiveService = archiveService;
        _logger = logger;

        RegisterCatalogHandlers();
    }

    private void RegisterCatalogHandlers()
    {
        // Responds with locally hosted visible files when probed by remote nodes
        _router.RegisterCommandHandler("CatalogGetLocalEntries", async (senderId, envelope, token) =>
        {
            var localTracked = await _archiveService.GetTrackedFilesAsync(includeHidden: false);
            var entries = localTracked.Select(t => new FileCatalogEntry
            {
                Manifest = t.Manifest,
                HostNodeId = _peerTracker.GetPeer(senderId)?.Identity.NodeId ?? string.Empty,
                IsLocalSeeding = true,
                DiscoveredUtc = DateTime.UtcNow
            }).ToList();

            return JsonSerializer.Serialize(entries);
        });
    }

    public async Task<List<FileCatalogEntry>> BrowseNetworkCatalogAsync()
    {
        var results = new Dictionary<string, FileCatalogEntry>(StringComparer.OrdinalIgnoreCase);

        // 1. Add local public files
        var localFiles = await _archiveService.GetTrackedFilesAsync(includeHidden: false);
        foreach (var file in localFiles)
        {
            results[file.Manifest.ContentHash] = new FileCatalogEntry
            {
                Manifest = file.Manifest,
                HostNodeId = "localhost",
                IsLocalSeeding = true,
                DiscoveredUtc = DateTime.UtcNow
            };
        }

        // 2. Discover nodes with "file_tracker" or "tracker" roles
        var trackerNodes = _peerTracker.GetAvailableNodes(PeerTracker.DefaultNodeComparer)
            .Where(n => n.Identity.Roles.Contains("file_tracker", StringComparer.OrdinalIgnoreCase) ||
                        n.Identity.Roles.Contains("tracker", StringComparer.OrdinalIgnoreCase))
            .ToList();

        foreach (var node in trackerNodes)
        {
            try
            {
                var remoteEntries = await QueryNodeCatalogDirectAsync(node.Identity.NodeId);
                foreach (var entry in remoteEntries)
                {
                    if (!results.ContainsKey(entry.Manifest.ContentHash))
                    {
                        results[entry.Manifest.ContentHash] = entry;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[FileCatalog] Failed querying catalog from tracker node {NodeId}", node.Identity.NodeId);
            }
        }

        return results.Values.ToList();
    }

    public async Task<List<FileCatalogEntry>> QueryNodeCatalogDirectAsync(string nodeId)
    {
        if (string.IsNullOrEmpty(nodeId)) return new List<FileCatalogEntry>();

        var routeResult = await _router.SendRequestAsync(
            nodeId,
            "CatalogGetLocalEntries",
            "{}",
            new RouteSettings { Timeout = TimeSpan.FromSeconds(8) });

        if (routeResult.IsSuccess && !string.IsNullOrEmpty(routeResult.Payload))
        {
            try
            {
                var entries = JsonSerializer.Deserialize<List<FileCatalogEntry>>(routeResult.Payload);
                if (entries != null)
                {
                    var peer = _peerTracker.GetPeer(nodeId);
                    foreach (var e in entries)
                    {
                        e.HostNodeId = nodeId;
                        e.HostSignalingEndpoints = peer?.Identity.SignalingEndpoints ?? new List<string>();
                        e.IsLocalSeeding = false;
                    }
                    return entries;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[FileCatalog] Error deserializing catalog response from {NodeId}", nodeId);
            }
        }

        return new List<FileCatalogEntry>();
    }

    public async Task<PTorrentManifest?> FetchManifestFromNodeAsync(string hostNodeId, string contentHash)
    {
        if (string.IsNullOrEmpty(hostNodeId) || string.IsNullOrEmpty(contentHash)) return null;

        var routeResult = await _router.SendRequestAsync(
            hostNodeId,
            "TrackerGetManifest",
            "{}",
            new RouteSettings { Timeout = TimeSpan.FromSeconds(10) });

        if (routeResult.IsSuccess && !string.IsNullOrEmpty(routeResult.Payload))
        {
            return PTorrentManifest.DeserializeFromJson(routeResult.Payload);
        }

        return null;
    }

    public void Dispose()
    {
        _router.UnregisterCommandHandler("CatalogGetLocalEntries");
    }
}