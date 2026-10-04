using FileTransferModule.Models.Torrent;
using FileTransferModule.Models;
using FileTransferModule.Services;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using P2PModule.Interfaces;
using P2PModule.Services.Maintenance;

using ProcessorApplication.Attributes;

namespace FileTransferModule.Controllers;

[ModuleRoute(FileTransferModuleEntryPoint.MODULE_ID)]
[Route("/[area]/[controller]/[action]/{id?}")]
[Authorize(Policy = "AdminLocalPolicy", Roles = "Admin")]
[ApiExplorerSettings(IgnoreApi = true)]
public class FileCatalogController : Controller
{
    private readonly IFileCatalogService _catalogService;
    private readonly ISwarmDownloadCoordinator _downloadCoordinator;
    private readonly INodeIdentityService _identityService;
    private readonly IGenericChannelSyncService _channelSyncService;
    private readonly IFileTrackerAnnounceService _announceService;

    public FileCatalogController(
        IFileCatalogService catalogService,
        ISwarmDownloadCoordinator downloadCoordinator,
        INodeIdentityService identityService,
        IGenericChannelSyncService channelSyncService,
        IFileTrackerAnnounceService announceService)
    {
        _catalogService = catalogService;
        _downloadCoordinator = downloadCoordinator;
        _identityService = identityService;
        _channelSyncService = channelSyncService;
        _announceService = announceService;
    }

    private const int DefaultPageSize = 24;

    [HttpGet]
    public async Task<IActionResult> Index(int page = 0, string? hostNodeId = null, string? search = null)
    {
        var result = await _catalogService.GetLocalCatalogPagedAsync(page, DefaultPageSize, hostNodeIdFilter: hostNodeId, fileNameSearch: search);

        ViewData["LocalNodeId"] = _identityService.NodeId;
        ViewData["LocalNodeRoles"] = string.Join(", ", _identityService.GetCapablities());

        if (Request.Headers["X-Requested-With"] == "XMLHttpRequest")
        {
            return PartialView("_CatalogGridPartial", result);
        }

        return View(result);
    }

    /// <summary>Directly probes one node's catalogue (bypassing local knowledge) and folds the lightweight result back into local storage - see class remarks on FileCatalogService.</summary>
    [HttpGet]
    public async Task<IActionResult> QueryNode(string nodeId, int page = 0)
    {
        if (string.IsNullOrWhiteSpace(nodeId))
        {
            return BadRequest("Node ID is required.");
        }

        var result = await _catalogService.QueryNodeCatalogPagedAsync(nodeId, page, DefaultPageSize);
        return PartialView("_CatalogGridPartial", result);
    }

    /// <summary>Re-reads local knowledge (same filters as Index) - for the "Refresh" button and pagination links, no live network probing.</summary>
    [HttpGet]
    public async Task<IActionResult> RefreshCatalog(int page = 0, string? hostNodeId = null, string? search = null)
    {
        var result = await _catalogService.GetLocalCatalogPagedAsync(page, DefaultPageSize, hostNodeIdFilter: hostNodeId, fileNameSearch: search);
        return PartialView("_CatalogGridPartial", result);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> StartDownload(string hostNodeId, string contentHash, string? customDirectory, string? customFileName)
    {
        if (string.IsNullOrWhiteSpace(contentHash))
        {
            return Json(new { success = false, message = "Content hash is required." });
        }

        try
        {
            // 1. Resolve manifest from remote host or tracker
            PTorrentManifest? manifest = await _catalogService.FetchManifestFromNodeAsync(hostNodeId, contentHash);
            if (manifest == null)
            {
                return Json(new { success = false, message = "Could not resolve .ptorrent manifest from target host." });
            }

            // 2. Admin privilege check for directed directory selection
            bool isLocalAdmin = User.IsInRole("Admin");

            // 3. Initiate swarm download task
            string taskId = await _downloadCoordinator.StartDownloadAsync(
                manifest,
                customTargetDirectory: customDirectory,
                customFileName: customFileName,
                isLocalAdmin: isLocalAdmin);

            return Json(new { success = true, taskId = taskId, fileName = manifest.FileName });
        }
        catch (Exception ex)
        {
            return Json(new { success = false, message = ex.Message });
        }
    }

    /// <summary>
    /// Runs one epidemic consensus sync pass for the file catalog channel against a peer (a random
    /// compatible peer is picked if none is given). Returns a ChannelSyncResult with pulled/rejected
    /// counts and a failure reason if the pass didn't complete.
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> TriggerCatalogConsensusSync(string? peerNodeId = null)
    {
        var result = await _channelSyncService.TriggerSyncAsync(FileCatalogConsensusChannel.CHANNEL_ID, peerNodeId);
        return Json(result);
    }

    /// <summary>
    /// Ensures the file catalog channel's continuous epidemic walk is running: seeds it from every
    /// currently known compatible peer and starts the background loop if it isn't already active.
    /// Idempotent - safe to call repeatedly. Returns whether the walk is running after the call.
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> EnsureCatalogWalkRunning()
    {
        await _channelSyncService.EnsureChannelWalkRunningAsync(FileCatalogConsensusChannel.CHANNEL_ID);
        bool isRunning = _channelSyncService.GetActiveWalkChannelIds().Contains(FileCatalogConsensusChannel.CHANNEL_ID, StringComparer.OrdinalIgnoreCase);
        return Json(new { running = isRunning });
    }

    /// <summary>
    /// Paginated, filterable read of everything currently known locally: own hosted files plus
    /// everything learned from other nodes. No network calls - purely local state.
    /// hostNodeId narrows to files known to be hosted by one node (pass this node's own id to see
    /// only local files); search is a case-insensitive substring match on the file name. Entries
    /// with hasFullInfo = false are lightweight records - use DownloadFileInfo to get the rest.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetLocalCatalogPaged(int page = 0, int pageSize = 50, string? hostNodeId = null, string? search = null)
    {
        var result = await _catalogService.GetLocalCatalogPagedAsync(page, pageSize, includeHidden: false, hostNodeIdFilter: hostNodeId, fileNameSearch: search);
        return Json(result);
    }

    /// <summary>
    /// Explicitly downloads the full info (manifest) for one file from its host and saves it
    /// against that file's catalog record. Consensus only ever brings in lightweight records, so
    /// this is how a record gets completed. Returns saved: false if it was fetched but the host's
    /// per-record cap prevented storing it.
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DownloadFileInfo(string hostNodeId, string contentHash)
    {
        if (string.IsNullOrWhiteSpace(hostNodeId) || string.IsNullOrWhiteSpace(contentHash))
        {
            return BadRequest("Host node id and content hash are required.");
        }

        var result = await _catalogService.DownloadFileInfoAsync(hostNodeId, contentHash);
        return Json(result);
    }

    /// <summary>
    /// Paginated catalogue request to one specific node: fetches its listing right now instead of
    /// waiting for epidemic convergence, and saves the full info of what comes back (up to the
    /// per-host cap - savedCount says how many were kept; the rest are still returned for display).
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> QueryNodeCatalogPaged(string nodeId, int page = 0, int pageSize = 50)
    {
        if (string.IsNullOrWhiteSpace(nodeId))
        {
            return BadRequest("Node ID is required.");
        }

        var result = await _catalogService.QueryNodeCatalogPagedAsync(nodeId, page, pageSize);
        return Json(result);
    }

    /// <summary>
    /// Current file-module role/capability status: whether this node advertises
    /// file_tracker/file_node, whether the catalog consensus channel's epidemic walk is currently
    /// running, and which peers are currently known to be file_tracker nodes.
    /// </summary>
    [HttpGet]
    public IActionResult GetFileModuleStatus()
    {
        var roles = _identityService.GetCapablities();
        return Json(new
        {
            nodeId = _identityService.NodeId,
            isFileTrackerRole = roles.Contains(FileTransferModuleEntryPoint.FileTracker, StringComparer.OrdinalIgnoreCase),
            isFileNodeRole = roles.Contains(FileTransferModuleEntryPoint.FileNode, StringComparer.OrdinalIgnoreCase),
            isCatalogWalkRunning = _channelSyncService.GetActiveWalkChannelIds().Contains(FileCatalogConsensusChannel.CHANNEL_ID, StringComparer.OrdinalIgnoreCase),
            knownTrackerPeerIds = _announceService.GetKnownTrackerPeerIds()
        });
    }

    /// <summary>
    /// Manually announces every locally-hosted file to every currently known file_tracker peer
    /// right now, independent of the periodic 10-minute loop. No-ops (returns announced: 0) if
    /// this node doesn't advertise "file_node".
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> TriggerAnnounceAll()
    {
        int count = await _announceService.TriggerAnnounceAllAsync();
        return Json(new { announced = count, fileNodeActive = _announceService.IsFileNodeActive() });
    }

    /// <summary>
    /// Starts downloading several files at once, each possibly from a different host. Resolves
    /// each item's manifest via FetchManifestFromNodeAsync before starting its task; failures for
    /// one item don't block the others - check each result's Success/Message. requestJson is a
    /// JSON-serialized BatchDownloadRequest, sent as a regular form field (not a raw JSON body) so
    /// the antiforgery token travels the same way as every other form post here.
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> StartBatchDownload(string requestJson)
    {
        BatchDownloadRequest? request;
        try
        {
            request = string.IsNullOrEmpty(requestJson) ? null : System.Text.Json.JsonSerializer.Deserialize<BatchDownloadRequest>(requestJson);
        }
        catch (Exception ex)
        {
            return Json(new { success = false, message = $"requestJson is not a valid BatchDownloadRequest: {ex.Message}", results = Array.Empty<BatchDownloadResultItem>() });
        }

        if (request?.Items == null || request.Items.Count == 0)
        {
            return Json(new { success = false, message = "No items provided.", results = Array.Empty<BatchDownloadResultItem>() });
        }

        bool isLocalAdmin = User.IsInRole("Admin");
        var results = new List<BatchDownloadResultItem>();

        foreach (var item in request.Items)
        {
            var result = new BatchDownloadResultItem { ContentHash = item.ContentHash };

            if (string.IsNullOrWhiteSpace(item.ContentHash))
            {
                result.Message = "Content hash is required.";
                results.Add(result);
                continue;
            }

            try
            {
                var manifest = await _catalogService.FetchManifestFromNodeAsync(item.HostNodeId, item.ContentHash);
                if (manifest == null)
                {
                    result.Message = "Could not resolve .ptorrent manifest from target host.";
                    results.Add(result);
                    continue;
                }

                string taskId = await _downloadCoordinator.StartDownloadAsync(
                    manifest,
                    customTargetDirectory: request.CustomDirectory,
                    customFileName: item.CustomFileName,
                    isLocalAdmin: isLocalAdmin);

                result.Success = true;
                result.TaskId = taskId;
                result.FileName = manifest.FileName;
            }
            catch (Exception ex)
            {
                result.Message = ex.Message;
            }

            results.Add(result);
        }

        return Json(new { success = results.Any(r => r.Success), results });
    }
}