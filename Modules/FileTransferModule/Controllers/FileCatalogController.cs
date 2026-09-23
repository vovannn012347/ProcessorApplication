using FileSharingModule.Models.Torrent;
using FileSharingModule.Services;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using P2PModule.Interfaces;

using ProcessorApplication.Attributes;

namespace FileSharingModule.Controllers;

[ModuleRoute(FileTrModuleEntryPoint.MODULE_ID)]
[Route("/[area]/[controller]/[action]/{id?}")]
[Authorize(Policy = "AdminLocalPolicy", Roles = "Admin")]
[ApiExplorerSettings(IgnoreApi = true)]
public class FileCatalogController : Controller
{
    private readonly IFileCatalogService _catalogService;
    private readonly ISwarmDownloadCoordinator _downloadCoordinator;
    private readonly INodeIdentityService _identityService;

    public FileCatalogController(
        IFileCatalogService catalogService,
        ISwarmDownloadCoordinator downloadCoordinator,
        INodeIdentityService identityService)
    {
        _catalogService = catalogService;
        _downloadCoordinator = downloadCoordinator;
        _identityService = identityService;
    }

    [HttpGet]
    public async Task<IActionResult> Index()
    {
        var entries = await _catalogService.BrowseNetworkCatalogAsync();

        ViewData["LocalNodeId"] = _identityService.NodeId;
        ViewData["LocalNodeRoles"] = string.Join(", ", _identityService.GetCapablities());

        if (Request.Headers["X-Requested-With"] == "XMLHttpRequest")
        {
            return PartialView("_CatalogGridPartial", entries);
        }

        return View(entries);
    }

    [HttpGet]
    public async Task<IActionResult> QueryNode(string nodeId)
    {
        if (string.IsNullOrWhiteSpace(nodeId))
        {
            return BadRequest("Node ID is required.");
        }

        var entries = await _catalogService.QueryNodeCatalogDirectAsync(nodeId);
        return PartialView("_CatalogGridPartial", entries);
    }

    [HttpGet]
    public async Task<IActionResult> RefreshCatalog()
    {
        var entries = await _catalogService.BrowseNetworkCatalogAsync();
        return PartialView("_CatalogGridPartial", entries);
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
}