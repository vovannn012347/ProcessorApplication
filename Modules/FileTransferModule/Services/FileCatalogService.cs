
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

using FileTransferModule.Configuration;
using FileTransferModule.Models;
using FileTransferModule.Models.Torrent;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using P2PModule.Interfaces;
using P2PModule.Services.Connection;
using P2PModule.Services.Persistence;

namespace FileTransferModule.Services;


/// <summary>
/// Allows browsing and querying available files across the network. Local browsing
/// (BrowseNetworkCatalogAsync / GetLocalCatalogPagedAsync) and direct node queries
/// (QueryNodeCatalogDirectAsync / QueryNodeCatalogPagedAsync) both read from and write into the
/// same underlying store - the file-catalog channel's local view (own hosted files + per-node
/// records under FileSharingSettings.CatalogPath).
///
/// Three distinct things travel over the wire, at three distinct weights:
///  - Catalog browsing (CatalogGetLocalEntriesPaged) - lightweight shells: ContentHash, FileName,
///    TotalSizeBytes, CreatedAtUtc. No block hashes, ever - that's what makes it cheap to browse a
///    whole page of files. Same weight whether it arrives via consensus relay or a direct probe.
///  - A file's description/custom fields (CatalogGetFileInfo / DownloadFileInfoAsync) - fetched
///    only on an explicit per-file request, never automatically.
///  - The actual .ptorrent manifest with block hashes (TrackerGetManifest / FetchManifestFromNodeAsync)
///    - fetched only when a download is actually starting. This is the only place block hashes
///    ever travel; they fully cover a file's parts (byte ranges and hashes) on their own, which is
///    what allows arbitrary block lengths.
/// </summary>
public interface IFileCatalogService
{
    /// <summary>
    /// Everything currently known locally: own hosted files plus everything learned via consensus
    /// relay or previous direct queries. No live network probing - see QueryNodeCatalogDirectAsync /
    /// TriggerCatalogConsensusSync (on IGenericChannelSyncService) for that.
    /// </summary>
    Task<List<FileCatalogEntry>> BrowseNetworkCatalogAsync();

    /// <summary>
    /// Directly probes a specific node for its hosted public files, bounded to
    /// FileSharingSettings.DirectQueryDefaultPageSize entries, and folds the (lightweight) result
    /// into local storage (respecting the per-host cap) before returning it.
    /// </summary>
    Task<List<FileCatalogEntry>> QueryNodeCatalogDirectAsync(string nodeId);

    /// <summary>
    /// Fetches the full .ptorrent manifest (with block hashes) from a specific host node - this is
    /// the dedicated download-intent path, separate from and never used by catalog browsing.
    /// Returns null if the host doesn't have it, or if what came back isn't the requested file or
    /// fails its integrity check.
    /// </summary>
    Task<PTorrentManifest?> FetchManifestFromNodeAsync(string hostNodeId, string contentHash);

    /// <summary>
    /// Explicitly fetches one file's description/custom fields from its host and saves them
    /// against that file's catalog record (subject to the per-host cap if the record wasn't
    /// already known). Never touches or requests the manifest/block hashes.
    /// </summary>
    Task<FileInfoDownloadResult> DownloadFileInfoAsync(string hostNodeId, string contentHash);

    /// <summary>
    /// Paginated direct probe of a specific node's hosted files - lighter-weight alternative to
    /// waiting for epidemic consensus convergence when you just need one node's current listing
    /// right now. Lightweight shells only (see class remarks); CatalogPageResponse.SavedCount says
    /// how many of the returned entries were newly saved locally (subject to the per-host cap).
    /// </summary>
    Task<CatalogPageResponse> QueryNodeCatalogPagedAsync(string nodeId, int page = 0, int pageSize = 50);

    /// <summary>
    /// Paginated, filterable view of everything currently known locally - own hosted files plus
    /// whatever has been learned via the file-catalog channel. No network calls.
    /// hostNodeIdFilter narrows to files known to be hosted by one specific node; fileNameSearch
    /// does a case-insensitive substring match on file name.
    /// </summary>
    Task<CatalogPageResponse> GetLocalCatalogPagedAsync(int page = 0, int pageSize = 50, bool includeHidden = false, string? hostNodeIdFilter = null, string? fileNameSearch = null);
}

public class FileCatalogService : IFileCatalogService, IDisposable
{
    private readonly IP2PRouterService _router;
    private readonly IPeerTracker _peerTracker;
    private readonly IFileArchiveService _archiveService;
    private readonly INodeIdentityService _identity;
    private readonly IFileCatalogConsensusChannel _catalogChannel;
    private readonly IOptionsMonitor<FileSharingSettings> _settings;
    private readonly ILogger<FileCatalogService> _logger;

    public FileCatalogService(
        IP2PRouterService router,
        IPeerTracker peerTracker,
        IFileArchiveService archiveService,
        INodeIdentityService identity,
        IFileCatalogConsensusChannel catalogChannel,
        IOptionsMonitor<FileSharingSettings> settings,
        ILogger<FileCatalogService> logger)
    {
        _router = router;
        _peerTracker = peerTracker;
        _archiveService = archiveService;
        _identity = identity;
        _catalogChannel = catalogChannel;
        _settings = settings;
        _logger = logger;

        RegisterCatalogHandlers();
    }

    private void RegisterCatalogHandlers()
    {
        // Responds with locally hosted visible files when probed by remote nodes. Lightweight
        // shells only - no block hashes, ever (see class remarks). Bounded to
        // DirectQueryDefaultPageSize as a protocol-level safety net even for callers using this
        // legacy unpaginated command instead of CatalogGetLocalEntriesPaged.
        _router.RegisterCommandHandler("CatalogGetLocalEntries", async (senderId, envelope, token) =>
        {
            var localTracked = OrderStable(await _archiveService.GetTrackedFilesAsync(includeHidden: false));
            var entries = localTracked
                .Take(Math.Max(1, _settings.CurrentValue.DirectQueryDefaultPageSize))
                .Select(ToShellEntry)
                .ToList();

            return JsonSerializer.Serialize(entries);
        });

        // Paginated variant - same locally-hosted data, but chunked. Prefer this over the
        // unpaginated command above for nodes that may be hosting many files.
        _router.RegisterCommandHandler("CatalogGetLocalEntriesPaged", async (senderId, envelope, token) =>
        {
            var req = string.IsNullOrEmpty(envelope.SerializedPayload)
                ? new CatalogPageRequest()
                : JsonSerializer.Deserialize<CatalogPageRequest>(envelope.SerializedPayload) ?? new CatalogPageRequest();

            var localTracked = OrderStable(await _archiveService.GetTrackedFilesAsync(includeHidden: false));
            var allEntries = localTracked
                .Where(t => !string.IsNullOrEmpty(t.Manifest?.ContentHash))
                .Select(ToShellEntry)
                .ToList();

            var page = BuildPage(allEntries, req.Page, req.PageSize);
            return JsonSerializer.Serialize(page);
        });

        // Explicit per-file info request - description/custom fields, never the manifest.
        _router.RegisterCommandHandler("CatalogGetFileInfo", async (senderId, envelope, token) =>
        {
            var response = new CatalogFileInfoResponse();

            var req = string.IsNullOrEmpty(envelope.SerializedPayload)
                ? null
                : JsonSerializer.Deserialize<CatalogFileInfoRequest>(envelope.SerializedPayload);

            if (req == null || string.IsNullOrEmpty(req.FileIdentifier))
            {
                return JsonSerializer.Serialize(response);
            }

            var local = await _archiveService.GetTrackedFileAsync(req.FileIdentifier);
            if (local == null || local.IsHidden)
            {
                return JsonSerializer.Serialize(response); // Found = false
            }

            response.Found = true;
            response.DisplayName = local.Manifest.FileName;
            response.Description = local.Description;
            response.CustomFields = local.CustomFields ?? new Dictionary<string, string>();

            return JsonSerializer.Serialize(response);
        });
    }

    /// <summary>Stable order so page N is the same slice on every request (the archive's dictionary isn't ordered).</summary>
    private static List<FileArchiveItemRecord> OrderStable(List<FileArchiveItemRecord> items)
        => items.OrderByDescending(t => t.RegisteredUtc).ThenBy(t => t.Manifest.ContentHash, StringComparer.Ordinal).ToList();

    /// <summary>
    /// The lightweight shape sent over the wire for catalog browsing: identity, name, size,
    /// created - never block hashes (BlockHashes/BlockSizeBytes/SliceSizeBytes/InitialPeers are
    /// deliberately left off PTorrentManifest here).
    /// </summary>
    private FileCatalogEntry ToShellEntry(FileArchiveItemRecord t) => new()
    {
        Manifest = new PTorrentManifest
        {
            ContentHash = t.Manifest.ContentHash,
            FileName = t.Manifest.FileName,
            TotalSizeBytes = t.Manifest.TotalSizeBytes,
            CreatedAtUtc = t.Manifest.CreatedAtUtc
        },
        HostNodeId = _identity.NodeId,
        HostSignalingEndpoints = _identity.GetEndpoints(),
        IsLocalSeeding = true,
        DiscoveredUtc = t.RegisteredUtc
    };

    private static CatalogPageResponse BuildPage(List<FileCatalogEntry> all, int page, int pageSize)
    {
        if (pageSize <= 0) pageSize = 50;
        if (page < 0) page = 0;

        var pageItems = all.Skip(page * pageSize).Take(pageSize).ToList();
        return new CatalogPageResponse
        {
            Entries = pageItems,
            Page = page,
            PageSize = pageSize,
            TotalCount = all.Count,
            HasMore = (page + 1) * pageSize < all.Count
        };
    }

    public async Task<List<FileCatalogEntry>> BrowseNetworkCatalogAsync()
    {
        // Local knowledge only - own hosted files plus everything already learned via consensus
        // relay or prior direct queries. No live network probing here; that's what
        // QueryNodeCatalogDirectAsync and TriggerSyncAsync (IGenericChannelSyncService) are for,
        // and both of those feed results back into this same store.
        return await _catalogChannel.GetAllKnownEntriesAsync();
    }

    public async Task<List<FileCatalogEntry>> QueryNodeCatalogDirectAsync(string nodeId)
    {
        if (string.IsNullOrEmpty(nodeId)) return new List<FileCatalogEntry>();

        int boundedSize = Math.Max(1, _settings.CurrentValue.DirectQueryDefaultPageSize);
        var page = await QueryNodeCatalogPagedAsync(nodeId, page: 0, pageSize: boundedSize);
        return page.Entries;
    }

    public async Task<PTorrentManifest?> FetchManifestFromNodeAsync(string hostNodeId, string contentHash)
    {
        if (string.IsNullOrEmpty(hostNodeId) || string.IsNullOrEmpty(contentHash)) return null;

        // The tracker handler looks up the manifest by the "contentHash" property of the request
        // body and returns nothing without it.
        var routeResult = await _router.SendRequestAsync(
            hostNodeId,
            "TrackerGetManifest",
            JsonSerializer.Serialize(new { contentHash }),
            new RouteSettings { Timeout = TimeSpan.FromSeconds(10) });

        if (!routeResult.IsSuccess || string.IsNullOrEmpty(routeResult.Payload)) return null;

        try
        {
            var manifest = PTorrentManifest.DeserializeFromJson(routeResult.Payload);
            if (manifest == null) return null;

            if (!string.Equals(manifest.ContentHash, contentHash, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("[FileCatalog] {NodeId} returned a manifest for a different file than requested ({Requested}).", hostNodeId, contentHash);
                return null;
            }

            if (!manifest.VerifyContentHash())
            {
                _logger.LogWarning("[FileCatalog] Manifest for {Hash} from {NodeId} failed its integrity check (hash doesn't match block hashes).", contentHash, hostNodeId);
                return null;
            }

            return manifest;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[FileCatalog] Could not parse manifest for {Hash} from {NodeId}.", contentHash, hostNodeId);
            return null;
        }
    }

    public async Task<FileInfoDownloadResult> DownloadFileInfoAsync(string hostNodeId, string contentHash)
    {
        if (string.IsNullOrWhiteSpace(hostNodeId) || string.IsNullOrWhiteSpace(contentHash))
            return new FileInfoDownloadResult { Message = "Host node id and content hash are required." };

        var reqPayload = new CatalogFileInfoRequest { FileIdentifier = contentHash };
        var routeResult = await _router.SendRequestAsync(
            hostNodeId,
            "CatalogGetFileInfo",
            JsonSerializer.Serialize(reqPayload),
            new RouteSettings { Timeout = TimeSpan.FromSeconds(10) });

        if (!routeResult.IsSuccess || string.IsNullOrEmpty(routeResult.Payload))
        {
            return new FileInfoDownloadResult { Message = "No response from host." };
        }

        CatalogFileInfoResponse? info;
        try
        {
            info = JsonSerializer.Deserialize<CatalogFileInfoResponse>(routeResult.Payload);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[FileCatalog] Could not parse file-info response for {Hash} from {NodeId}.", contentHash, hostNodeId);
            return new FileInfoDownloadResult { Message = "Malformed response from host." };
        }

        if (info == null || !info.Found)
        {
            return new FileInfoDownloadResult { Message = "Host does not have this file (anymore)." };
        }

        bool saved = await _catalogChannel.SaveFullInfoAsync(hostNodeId, contentHash, info.DisplayName, info.Description, info.CustomFields);

        var entry = new FileCatalogEntry
        {
            Manifest = new PTorrentManifest { ContentHash = contentHash, FileName = info.DisplayName },
            HostNodeId = hostNodeId,
            HostSignalingEndpoints = _peerTracker.GetPeer(hostNodeId)?.Identity.SignalingEndpoints ?? new List<string>(),
            IsLocalSeeding = false,
            Description = info.Description,
            CustomFields = info.CustomFields,
            FullInfoFetchedUtc = DateTime.UtcNow
        };

        return new FileInfoDownloadResult
        {
            Success = true,
            Saved = saved,
            Entry = entry,
            Message = saved ? null : "Fetched, but not saved (per-host record cap reached)."
        };
    }

    public async Task<CatalogPageResponse> QueryNodeCatalogPagedAsync(string nodeId, int page = 0, int pageSize = 50)
    {
        if (string.IsNullOrEmpty(nodeId)) return new CatalogPageResponse { Page = page, PageSize = pageSize };

        var req = new CatalogPageRequest { Page = page, PageSize = pageSize };
        var routeResult = await _router.SendRequestAsync(
            nodeId,
            "CatalogGetLocalEntriesPaged",
            JsonSerializer.Serialize(req),
            new RouteSettings { Timeout = TimeSpan.FromSeconds(10) });

        if (!routeResult.IsSuccess || string.IsNullOrEmpty(routeResult.Payload))
        {
            return new CatalogPageResponse { Page = page, PageSize = pageSize };
        }

        try
        {
            var result = JsonSerializer.Deserialize<CatalogPageResponse>(routeResult.Payload) ?? new CatalogPageResponse { Page = page, PageSize = pageSize };

            var peer = _peerTracker.GetPeer(nodeId);
            var peerEndpoints = peer?.Identity.SignalingEndpoints ?? new List<string>();

            foreach (var e in result.Entries)
            {
                e.HostNodeId = nodeId;
                e.HostSignalingEndpoints = peerEndpoints;
                e.IsLocalSeeding = false;

                // A direct catalogue probe - lightweight, same as consensus relay. Full info
                // (description/fields) is a separate, per-file explicit request (DownloadFileInfoAsync).
                if (!string.IsNullOrEmpty(e.Manifest?.ContentHash) &&
                    await _catalogChannel.SaveDirectlyObservedRecordAsync(nodeId, e.Manifest.ContentHash, e.Manifest.FileName, e.DiscoveredUtc))
                {
                    result.SavedCount++;
                }
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[FileCatalog] Error deserializing paged catalog response from {NodeId}", nodeId);
            return new CatalogPageResponse { Page = page, PageSize = pageSize };
        }
    }

    public async Task<CatalogPageResponse> GetLocalCatalogPagedAsync(int page = 0, int pageSize = 50, bool includeHidden = false, string? hostNodeIdFilter = null, string? fileNameSearch = null)
    {
        // Reads the file-catalog channel's combined view (own local files + everything learned via
        // relay/direct query) - a "list files" read of local state, no network calls.
        // Note: includeHidden has no effect on remote-host entries (they were never hidden to
        // begin with); it only ever would have affected our own files, and those are already
        // excluded upstream in FileArchiveService.GetTrackedFilesAsync(includeHidden:false) before
        // they reach the channel - kept as a parameter for forward-compat/explicitness.
        var all = await _catalogChannel.GetAllKnownEntriesAsync();

        if (!string.IsNullOrWhiteSpace(hostNodeIdFilter))
        {
            all = all.Where(e => string.Equals(e.HostNodeId, hostNodeIdFilter, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        if (!string.IsNullOrWhiteSpace(fileNameSearch))
        {
            all = all.Where(e => e.Manifest?.FileName != null &&
                                  e.Manifest.FileName.Contains(fileNameSearch, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        return BuildPage(all, page, pageSize);
    }

    public void Dispose()
    {
        _router.UnregisterCommandHandler("CatalogGetLocalEntries");
        _router.UnregisterCommandHandler("CatalogGetLocalEntriesPaged");
        _router.UnregisterCommandHandler("CatalogGetFileInfo");
    }
}
