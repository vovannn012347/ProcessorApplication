using FileTransferModule.Services;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

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

    public FileExchangeController(
        ISwarmDownloadCoordinator downloadCoordinator,
        ISwarmOrchestratorService swarmOrchestrator)
    {
        _downloadCoordinator = downloadCoordinator;
        _swarmOrchestrator = swarmOrchestrator;
    }

    [HttpGet]
    public IActionResult Index()
    {
        var tasks = _downloadCoordinator.GetAllTasks();

        if (Request.Headers["X-Requested-With"] == "XMLHttpRequest")
        {
            return PartialView("_ActiveTransfersPartial", tasks);
        }

        return View(tasks);
    }

    [HttpGet]
    public IActionResult GetTransfersTelemetry()
    {
        var tasks = _downloadCoordinator.GetAllTasks();
        return PartialView("_ActiveTransfersPartial", tasks);
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
}