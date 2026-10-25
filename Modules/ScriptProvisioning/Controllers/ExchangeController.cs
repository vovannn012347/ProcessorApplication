using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using ProcessorApplication.Attributes;

using ScriptProvisioning.Services;

namespace ScriptProvisioning.Controllers;

/// <summary>Script exchange page: publish installed builds, find and install packages from other nodes.</summary>
[ModuleRoute(ScriptProvisioningModuleEntryPoint.MODULE_ID)]
[Route("/[area]/[controller]/[action]/{id?}")]
[Authorize(Policy = "AdminLocalPolicy", Roles = "Admin")]
[ApiExplorerSettings(IgnoreApi = true)]
public class ExchangeController : Controller
{
    private readonly IScriptPublishingService _publishing;
    private readonly IScriptAcquisitionService _acquisition;

    public ExchangeController(IScriptPublishingService publishing, IScriptAcquisitionService acquisition)
    {
        _publishing = publishing;
        _acquisition = acquisition;
    }

    [HttpGet]
    public IActionResult Index()
    {
        if (Request.Headers["X-Requested-With"] == "XMLHttpRequest") return PartialView();
        return View();
    }

    [HttpGet]
    public async Task<IActionResult> Builds(CancellationToken ct) => Json(await _publishing.GetBuildsAsync(ct));

    [HttpGet]
    public async Task<IActionResult> Remote(CancellationToken ct) => Json(await _acquisition.GetRemotePackagesAsync(ct));

    [HttpGet]
    public IActionResult Tasks() => Json(_acquisition.GetTasks());

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Publish(int buildId, bool? hidden = null)
    {
        // Not tied to the request: packing a large build shouldn't be cut off by a closed tab
        var result = await _publishing.PublishAsync(buildId, hidden, CancellationToken.None);
        return Json(new { success = result.Success, message = result.Message });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Unpublish(int buildId)
    {
        var result = await _publishing.UnpublishAsync(buildId, CancellationToken.None);
        return Json(new { success = result.Success, message = result.Message });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Probe(string nodeId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(nodeId)) return Json(new { success = false, message = "Enter a node ID." });
        try
        {
            int found = await _acquisition.ProbeNodeAsync(nodeId.Trim(), ct);
            return Json(new { success = true, message = $"The node offers {found} script package(s)." });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Json(new { success = false, message = "Probe failed: " + ex.Message });
        }
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Install(string contentHash, bool replace = false)
    {
        if (string.IsNullOrWhiteSpace(contentHash)) return Json(new { success = false, message = "No package selected." });
        var task = await _acquisition.StartInstallAsync(contentHash, replace, CancellationToken.None);
        return Json(new { success = task.State != Models.InstallTaskState.Failed, message = task.Message, taskId = task.Id });
    }
}
