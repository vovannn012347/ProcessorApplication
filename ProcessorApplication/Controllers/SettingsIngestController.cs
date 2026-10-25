using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using ProcessorApplication.Attributes;
using ProcessorApplication.Configuration.SettingsIngest;
using ProcessorApplication.Utils;

namespace ProcessorApplication.Controllers;

/// <summary>
/// Admin page: shows where the module settings files differ from the stored settings and re-ingests them.
/// /Main/SettingsIngest/Index
/// </summary>
[Authorize(Policy = "AdminLocalPolicy")]
[ModuleRoute("Main")]
[Route("/[area]/[controller]/[action]/{id?}")]
public class SettingsIngestController : Controller
{
    private readonly ISettingsIngestService _ingest;
    private readonly ILogger<SettingsIngestController> _logger;

    public SettingsIngestController(ISettingsIngestService ingest, ILogger<SettingsIngestController> logger)
    {
        _ingest = ingest;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var model = new SettingsIngestViewModel
        {
            Preview = await _ingest.PreviewAsync(null, ct),
            StartupMode = (_ingest as SettingsIngestService)?.Options.Mode
        };
        return Request.IsAjaxRequest() ? PartialView(model) : View(model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Apply([FromForm] SettingsIngestMode mode, [FromForm] string? area, CancellationToken ct)
    {
        if (mode == SettingsIngestMode.Off)
            return BadRequest();

        area = string.IsNullOrWhiteSpace(area) ? null : area.Trim();
        var applied = await _ingest.ApplyAsync(mode, area, ct);

        _logger.LogInformation("[Settings ingest] {User} applied {Count} setting(s) from files (mode {Mode}, area {Area}).",
            User.Identity?.Name, applied.AppliedCount, mode, area ?? "all");

        var model = new SettingsIngestViewModel
        {
            Preview = await _ingest.PreviewAsync(null, ct),
            StartupMode = (_ingest as SettingsIngestService)?.Options.Mode,
            LastApply = applied
        };
        return Request.IsAjaxRequest() ? PartialView("Index", model) : View("Index", model);
    }
}

public class SettingsIngestViewModel
{
    public SettingsIngestResult Preview { get; set; } = new();
    public SettingsIngestResult? LastApply { get; set; }
    public SettingsIngestMode? StartupMode { get; set; }
}
