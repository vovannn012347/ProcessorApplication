using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using ProcessorApplication.Attributes;
using ProcessorApplication.Configuration.SettingsIngest;
using ProcessorApplication.Utils;

namespace ProcessorApplication.Controllers;

/// <summary>
/// Admin page: every stored setting of every module in one list (edit, add, delete), next to the settings files'
/// values, plus re-ingest of the settings files. /Main/SettingsIngest/Index
/// </summary>
[Authorize(Policy = "AdminLocalPolicy")]
[ModuleRoute("Main")]
[Route("/[area]/[controller]/[action]/{id?}")]
public class SettingsIngestController : Controller
{
    private readonly ISettingsIngestService _ingest;
    private readonly ISettingsStore _store;
    private readonly ILogger<SettingsIngestController> _logger;

    public SettingsIngestController(ISettingsIngestService ingest, ISettingsStore store, ILogger<SettingsIngestController> logger)
    {
        _ingest = ingest;
        _store = store;
        _logger = logger;
    }

    private async Task<SettingsIngestViewModel> BuildModelAsync(SettingsIngestResult? lastApply, CancellationToken ct) => new()
    {
        Preview = await _ingest.PreviewAsync(null, ct),
        StartupMode = (_ingest as SettingsIngestService)?.Options.Mode,
        LastApply = lastApply,
        Rows = await _store.ListAsync(ct),
        Areas = (_store as SettingsStore)?.Areas ?? Array.Empty<string>()
    };

    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var model = await BuildModelAsync(null, ct);
        return Request.IsAjaxRequest() ? PartialView(model) : View(model);
    }

    /// <summary>Creates or changes one stored value (add=true: only if it doesn't exist yet). Takes effect immediately.</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetValue([FromForm] string area, [FromForm] string key, [FromForm] string? value,
                                              [FromForm] bool add, CancellationToken ct)
    {
        var error = await _store.SetAsync(area, key, value, add, ct);
        if (error == null)
            _logger.LogInformation("[Settings] {User} {Action} {Area}:{Key}.", User.Identity?.Name, add ? "added" : "changed", area, key);
        return Json(new { ok = error == null, error });
    }

    /// <summary>Deletes one stored value; the settings file's value (if any) applies again.</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteValue([FromForm] string area, [FromForm] string key, CancellationToken ct)
    {
        var error = await _store.DeleteAsync(area, key, ct);
        if (error == null)
            _logger.LogInformation("[Settings] {User} deleted {Area}:{Key}.", User.Identity?.Name, area, key);
        return Json(new { ok = error == null, error });
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

        var model = await BuildModelAsync(applied, ct);
        return Request.IsAjaxRequest() ? PartialView("Index", model) : View("Index", model);
    }
}

public class SettingsIngestViewModel
{
    public SettingsIngestResult Preview { get; set; } = new();
    public SettingsIngestResult? LastApply { get; set; }
    public SettingsIngestMode? StartupMode { get; set; }
    public List<StoredSettingRow> Rows { get; set; } = new();
    public IReadOnlyList<string> Areas { get; set; } = Array.Empty<string>();
}
