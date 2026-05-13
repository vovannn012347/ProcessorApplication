using System.Security.Claims;
using System.Text.Json;

using Common.Interfaces;
using Common.Interfaces.Menu;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

using ProcessorApplication.Attributes;
using ProcessorApplication.Dashboard;
using ProcessorApplication.Database.Models;
using ProcessorApplication.Infrastructure;

namespace ProcessorApplication.Controllers;


[ModuleRoute("Main")]
[Route("Dashboard/[action]/{id?}")]
public class DashboardController : Controller
{
    private readonly IEnumerable<IWidgetProvider> _widgetProviders;
    private readonly IDashboardSessionManager _sessionManager;
    private readonly IDashboardRepository _repo; // Dedicated persistence service

    public DashboardController(
        IEnumerable<IWidgetProvider> widgetProviders,
        IDashboardSessionManager sessionManager,
        IDashboardRepository db)
    {
        _widgetProviders = widgetProviders;
        _sessionManager = sessionManager;
        _repo = db;
    }

    /**
     * STEP 1: Get the Catalog
     * Returns what widgets exist and their basic metadata + saved layout
     */
    //[Route("GetCatalog")]
    [HttpGet]
    public IActionResult GetCatalog()
    {
        var user = User;
        bool isAuthenticated = user.Identity?.IsAuthenticated ?? false;

        var catalog = _widgetProviders
            .SelectMany(p => p.GetWidgetManifests())
            .Where(w => CheckPermissions(w, user))
            .Select(w => new
            {
                w.Id,
                w.Name,
                w.IconClass,
                w.DefaultOrder,
                w.ScriptPath
            })
            .OrderBy(w => w.DefaultOrder)
            .ToList();

        return Json(catalog);
    }

    [HttpGet]
    public async Task<IActionResult> GetUserSettings([FromQuery] string[] ids)
    {
        var userId = User.Identity?.Name;
        var dbSettings = await _repo.GetWidgetSettingsAsync(userId);

        // Filter by requested IDs
        var filtered = dbSettings.Where(s => ids.Contains(s.WidgetId));

        var result = filtered.ToDictionary(
            s => s.WidgetId,
            s => new
            {
                general = string.IsNullOrEmpty(s.GeneralSettingsJson) ? null : JsonSerializer.Deserialize<object>(s.GeneralSettingsJson),
                small = string.IsNullOrEmpty(s.SmallScreenSettingsJson) ? null : JsonSerializer.Deserialize<object>(s.SmallScreenSettingsJson),
                large = string.IsNullOrEmpty(s.LargeScreenSettingsJson) ? null : JsonSerializer.Deserialize<object>(s.LargeScreenSettingsJson)
            }
        );

        return Json(result);
    }

    [HttpPost]
    public async Task<IActionResult> SaveWidgetSettings([FromBody] UserWidgetSetting setting)
    {
        setting.UserId = User.Identity?.Name;

        var provider = _widgetProviders.FirstOrDefault(p => p.HasWidget(setting.WidgetId));
        if (provider == null) return NotFound();

        var manifest = provider.GetWidget(setting.UserId, setting.WidgetId).Manifest;
        if (manifest == null || !CheckPermissions(manifest, User)) return Forbid();

        await _repo.UpdateWidgetSettingAsync(setting);
        return Ok();
    }
    /**
     * STEP 2: Fetch Widget View (Skeleton)
     */
    [HttpGet]
    public IActionResult GetWidgetView(string widgetId)
    {
        var UserId = User.Identity?.Name;
        var provider = _widgetProviders.FirstOrDefault(p => p.HasWidget(widgetId));
        if (provider == null) return NotFound();

        var widget = provider.GetWidget(UserId, widgetId).Manifest;
        if (widget == null) return NotFound();

        if (!CheckPermissions(widget, User)) return Forbid();

        return PartialView(widget.ViewPath);
    }

    /**
     * STEP 3: Granular Update request by hand / State Retrieval
     * This is where the "Live Memory" access is forced by hand
     */
    [HttpGet]
    public async Task<IActionResult> GetUpdate(string widgetId)
    {
        var userId = User.Identity?.Name;
        var session = _sessionManager.GetSession(userId);
        if (session == null) return Unauthorized();

        // Find the provider responsible for this widget
        var provider = _widgetProviders.FirstOrDefault(p => p.HasWidget(widgetId));
        if (provider == null) return NotFound();

        // The provider gets access to the session to look up cached state
        var update = await provider.GetUpdatesAsync(userId, new[] { widgetId });

        // Record that this widget was specifically updated in this heartbeat cycle
        _sessionManager.Heartbeat(userId);

        return Json(update.ContainsKey(widgetId) ? update[widgetId] : null);
    }

    private bool CheckPermissions(WidgetManifest manifest, System.Security.Claims.ClaimsPrincipal user)
    {
        if (string.IsNullOrEmpty(manifest.Roles)) return true;
        var requiredRoles = manifest.Roles.Split(',', StringSplitOptions.RemoveEmptyEntries);
        return requiredRoles.Any(role => user.IsInRole(role.Trim()));
    }
}