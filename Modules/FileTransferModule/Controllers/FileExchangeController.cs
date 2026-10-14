using FileTransferModule.Configuration;
using FileTransferModule.Models;
using FileTransferModule.Services;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

using ProcessorApplication.Attributes;

namespace FileTransferModule.Controllers;

[ModuleRoute(FileTransferModuleEntryPoint.MODULE_ID)]
[Route("/[area]/[controller]/[action]/{id?}")]
[Authorize(Policy = "AdminLocalPolicy", Roles = "Admin")]
[ApiExplorerSettings(IgnoreApi = true)]
public class FileExchangeController : Controller
{
    private readonly ISwarmDownloadCoordinator _downloadCoordinator;
    private readonly ISwarmOrchestratorService _swarmOrchestrator;
    private readonly IFilePeerReputationService _reputation;
    private readonly IOptionsMonitor<FileSharingSettings> _settings;

    public FileExchangeController(
        ISwarmDownloadCoordinator downloadCoordinator,
        ISwarmOrchestratorService swarmOrchestrator,
        IFilePeerReputationService reputation,
        IOptionsMonitor<FileSharingSettings> settings)
    {
        _downloadCoordinator = downloadCoordinator;
        _swarmOrchestrator = swarmOrchestrator;
        _reputation = reputation;
        _settings = settings;
    }

    /// <summary>One snapshot of everything on the Active Transfers page; all numbers computed now.</summary>
    private ExchangeTelemetryViewModel BuildTelemetry() => new()
    {
        Tasks = _downloadCoordinator.GetAllTasks(),
        Uploads = _swarmOrchestrator.GetActiveUploads(),
        NodeDownload = _downloadCoordinator.GetTotalDownloadStats(),
        NodeUpload = _swarmOrchestrator.GetTotalUploadStats(),
        UploadSlotsInUse = _swarmOrchestrator.ActiveUploadSlots,
        UploadSlotsMax = Math.Max(1, _settings.CurrentValue.MaxUploadSlots),
        ActiveDownloads = _downloadCoordinator.RunningDownloads,
        MaxActiveDownloads = Math.Max(1, _settings.CurrentValue.MaxActiveDownloads)
    };

    [HttpGet]
    public IActionResult Index()
    {
        var model = BuildTelemetry();

        if (Request.Headers["X-Requested-With"] == "XMLHttpRequest")
        {
            return PartialView("_ActiveTransfersPartial", model);
        }

        return View(model);
    }

    [HttpGet]
    public IActionResult GetTransfersTelemetry()
    {
        return PartialView("_ActiveTransfersPartial", BuildTelemetry());
    }

    [HttpGet]
    public IActionResult GetBitfieldState(string taskId)
    {
        if (string.IsNullOrWhiteSpace(taskId)) return BadRequest();

        var state = _downloadCoordinator.GetTaskState(taskId);
        if (state == null) return NotFound();

        return PartialView("_BitfieldVisualizerPartial", state);
    }

    [HttpGet]
    public IActionResult GetPeerLanes(string taskId)
    {
        if (string.IsNullOrWhiteSpace(taskId)) return BadRequest();

        var state = _downloadCoordinator.GetTaskState(taskId);
        if (state == null) return NotFound();

        return PartialView("_PeerLanesPartial", state);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Pause(string taskId)
    {
        if (string.IsNullOrWhiteSpace(taskId)) return BadRequest();
        await _downloadCoordinator.PauseDownloadAsync(taskId);
        return Json(new { success = true });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Resume(string taskId)
    {
        if (string.IsNullOrWhiteSpace(taskId)) return BadRequest();
        await _downloadCoordinator.ResumeDownloadAsync(taskId);
        return Json(new { success = true });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Cancel(string taskId)
    {
        if (string.IsNullOrWhiteSpace(taskId)) return BadRequest();
        await _downloadCoordinator.CancelDownloadAsync(taskId);
        return Json(new { success = true });
    }

    /// <summary>
    /// File-source reputation of every peer this node has downloaded from (best first). Separate
    /// from the P2P node rating - only reflects block verification results and slice timeouts.
    /// </summary>
    [HttpGet]
    public IActionResult GetPeerReputations()
    {
        var now = DateTime.UtcNow;
        return Json(_reputation.GetAll().Select(r => new
        {
            nodeId = r.NodeId,
            score = Math.Round(r.Score, 2),
            blocksVerified = r.BlocksVerified,
            bytesVerified = r.BytesVerified,
            badBlocksSoleSource = r.BadBlocksSoleSource,
            badBlocksSharedSource = r.BadBlocksSharedSource,
            sliceTimeouts = r.SliceTimeouts,
            isBanned = r.IsBanned(now),
            bannedUntilUtc = r.BannedUntilUtc,
            lastUpdatedUtc = r.LastUpdatedUtc
        }));
    }

    /// <summary>Peer file-reputation page (per peer, across all files). Server-rendered; refresh to update.</summary>
    [HttpGet]
    public IActionResult PeerReputation()
    {
        return View(_reputation.GetAll());
    }

    /// <summary>Form post from the reputation page: reset/unban one peer, then back to the page.</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult ResetPeerReputationAndReturn(string nodeId)
    {
        if (!string.IsNullOrWhiteSpace(nodeId) && _reputation.Reset(nodeId))
        {
            TempData["ReputationMessage"] = "Reputation for that peer was reset.";
        }
        return Redirect($"/{FileTransferModuleEntryPoint.MODULE_ID}/FileExchange/PeerReputation");
    }

    /// <summary>Forgets a peer's file-source reputation, lifting any ban.</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult ResetPeerReputation(string nodeId)
    {
        if (string.IsNullOrWhiteSpace(nodeId)) return BadRequest();
        return Json(new { success = _reputation.Reset(nodeId) });
    }
}