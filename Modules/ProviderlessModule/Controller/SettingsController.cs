using System.Reflection;

using Common.Interfaces;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Win32;

using ProcessorApplication.Attributes;
using ProcessorApplication.Utils;

using ProviderlessModule.Configuration;
using ProviderlessModule.Configuration.Registry;
using ProviderlessModule.Configuration.Tunnel;
using ProviderlessModule.Infrastructure.Interfaces;
using ProviderlessModule.Models;

namespace ProviderlessModule.Controllers;

[Authorize(Policy = "AdminLocalPolicy")]
[ModuleRoute("Providerless")]
[Route("[controller]/[action]/{id?}")]
public class SettingsController : Controller
{
    public string ModuleId => ProviderlessModule.MODULE_ID;

    private readonly ISettingService _settingsService;
    private readonly IOptionsMonitor<PortalAccessSettings> _general;

    public SettingsController(
        ISettingService settingsService,
        IOptionsMonitor<PortalAccessSettings> general)
    {
        _settingsService = settingsService;
        _general = general;
    }

    public IActionResult Index()
    {
        var model = new ProviderlessSettingsViewModel
        {
            General = _general.CurrentValue,
            RegistryOptions = new List<SelectListItem>(),
            TunnelOptions = new List<SelectListItem>()
        };

        // --- THE EXTENSIBLE SCAN ---
        // We scan all assemblies but filter out any types that don't belong 
        // to the "Providerless" ecosystem.
        var settingsTypes = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(a => {
                    try { return a.GetTypes(); }
                    catch { return Type.EmptyTypes; }
                })
                .Where(t => typeof(IProviderSettings).IsAssignableFrom(t)
                       && !t.IsInterface && !t.IsAbstract
                       // Filter to ensure we only get Providerless-related types
                       // This allows for plugins like 'Providerless.Plugins.S3'
                       && (t.Namespace?.Contains("Providerless") ?? false));

        foreach (var type in settingsTypes)
        {
            var monitorType = typeof(IOptionsMonitor<>).MakeGenericType(type);
            var monitor = HttpContext.RequestServices.GetService(monitorType);
            if (monitor == null) continue;

            var currentValue = (IProviderSettings)monitorType.GetProperty("CurrentValue")?.GetValue(monitor);
            if (currentValue == null) continue;

            string key = currentValue.ProviderKey;
            var listItem = new SelectListItem(currentValue.ProviderName, key);

            if (currentValue is IRegistrySettings)
            {
                model.RegistrySettings[key] = currentValue;
                model.RegistryOptions.Add(listItem);
            }
            else if (currentValue is ITunnelSettings)
            {
                model.TunnelSettings[key] = currentValue;
                model.TunnelOptions.Add(listItem);
            }
        }

        model.RegistryOptions = model.RegistryOptions.OrderBy(x => x.Text).ToList();
        model.TunnelOptions = model.TunnelOptions.OrderBy(x => x.Text).ToList();

        return Request.IsAjaxRequest() ? PartialView(model) : View(model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken] // Security best practice for form posts
    public async Task<IActionResult> SaveProviderless(ProviderlessSettingsViewModel model)
    {
        CleanPluginModelState(model);

        if (!ModelState.IsValid)
        {
            return Request.IsAjaxRequest() ? PartialView("Index", model) : View("Index", model);
        }

        try
        {
            _settingsService.SetAutoUpdate(false);

            // 1. Save Core Settings (Partitioned by ModuleId)
            await SaveGenericObject(model.General, nameof(PortalAccessSettings));

            // 2. Save Active Registry
            var activeReg = model.General.ActiveRegistry?.ToLower();
            if (!string.IsNullOrEmpty(activeReg) && model.RegistrySettings.TryGetValue(activeReg, out var regObj))
            {
                await SaveGenericObject(regObj, nameof(PortalAccessSettings));
            }

            // 3. Save Selected Tunnel
            var activeTun = model.General.SelectedTunnel?.ToLower();
            if (!string.IsNullOrEmpty(activeTun) && model.TunnelSettings.TryGetValue(activeTun, out var tunObj))
            {
                await SaveGenericObject(tunObj, nameof(PortalAccessSettings));
            }

            _settingsService.SetAutoUpdate(true);
            _settingsService.ForceUpdateOptionsMonitor();

            TempData["SuccessMessage"] = "Configuration synchronized.";
        }
        catch (Exception ex)
        {
            ModelState.AddModelError("", $"Persistence error: {ex.Message}");
        }

        return Request.IsAjaxRequest() ? PartialView("Index", model) : View("Index", model);
    }

    /// <summary>
    /// Reflection-based persistence engine.
    /// Maps object properties to ISettingService keys automatically.
    /// </summary>
    /// <param name="obj">The settings object (e.g., CloudflareSettings instance).</param>
    /// <param name="sectionName">The root configuration section (e.g., "PortalAccessSettings").</param>

    private async Task SaveGenericObject(object obj, string sectionName)
    {
        if (obj == null) return;
        var properties = obj.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance);

        foreach (var prop in properties)
        {
            if (!prop.CanRead) continue;
            var value = prop.GetValue(obj)?.ToString() ?? string.Empty;
            string settingKey = $"{sectionName}:{prop.Name}";

            // Critical: Ensure the setting is saved to the 'Providerless' module table
            await _settingsService.SetAsync(ModuleId, settingKey, value);
        }
    }

    private void CleanPluginModelState(ProviderlessSettingsViewModel model)
    {
        // Ensure we have the current active keys to compare against
        var activeReg = model.General?.ActiveRegistry?.ToLower();
        var activeTun = model.General?.SelectedTunnel?.ToLower();

        foreach (var key in ModelState.Keys.ToList())
        {
            // 1. Always keep validation errors for General settings
            if (key.StartsWith(nameof(model.General))) continue;

            // 2. Identify if the key belongs to a plugin dictionary
            bool isRegistrySetting = key.Contains(nameof(model.RegistrySettings));
            bool isTunnelSetting = key.Contains(nameof(model.TunnelSettings));

            // 3. Determine if this specific key belongs to the ACTIVE plugins
            bool isCurrentRegistry = isRegistrySetting && key.Contains($"[{activeReg}]", StringComparison.OrdinalIgnoreCase);
            bool isCurrentTunnel = isTunnelSetting && key.Contains($"[{activeTun}]", StringComparison.OrdinalIgnoreCase);

            // 4. If it's a plugin setting but NOT the one being saved, suppress its errors
            if ((isRegistrySetting && !isCurrentRegistry) || (isTunnelSetting && !isCurrentTunnel))
            {
                // ClearValidationState removes errors and resets the state to 'Unvalidated',
                // but critically PRESERVES the 'AttemptedValue' so the UI doesn't clear.
                ModelState.ClearValidationState(key);
            }
        }
    }
}