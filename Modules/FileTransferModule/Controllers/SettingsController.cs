using Common.Interfaces;

using FileTransferModule.Configuration;
using FileTransferModule.Models;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

using ProcessorApplication.Attributes;

namespace FileTransferModule.Controllers;

[ModuleRoute(FileTransferModuleEntryPoint.MODULE_ID)]
[Route("/[area]/[controller]/[action]/{id?}")]
[Authorize(Policy = "AdminLocalPolicy", Roles = "Admin")]
[ApiExplorerSettings(IgnoreApi = true)]
public class SettingsController : Controller
{
    public string ModuleId => FileTransferModuleEntryPoint.MODULE_ID;

    private readonly ISettingService _settingsService;
    private readonly IOptionsMonitor<FileSharingSettings> _fileSharingSettings;

    public SettingsController(
        ISettingService settingsService,
        IOptionsMonitor<FileSharingSettings> fileSharingSettings)
    {
        _settingsService = settingsService;
        _fileSharingSettings = fileSharingSettings;
    }

    [HttpGet]
    public IActionResult Index()
    {
        var model = _fileSharingSettings.CurrentValue ?? new FileSharingSettings();
        return View(model);
    }

    /// <summary>
    /// Persists the general settings form and returns the same form fragment back (with a
    /// success/error banner and current values), for the calling JS to swap into
    /// #general-settings-section - same targeted-swap approach P2PModule's SaveP2P uses, just
    /// scoped to this one section instead of the whole page, so nothing gets nested inside itself.
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveSettings([FromForm] FileSharingSettings model)
    {
        if (model == null)
        {
            ViewData["ErrorMessage"] = "Invalid configuration data received.";
            return PartialView("_GeneralSettingsFormPartial", _fileSharingSettings.CurrentValue ?? new FileSharingSettings());
        }

        try
        {
            _settingsService.SetAutoUpdate(false);
            await _settingsService.SetAsync(ModuleId, $"{nameof(FileSharingSettings)}:{nameof(model.StorageRootPath)}", model.StorageRootPath);
            await _settingsService.SetAsync(ModuleId, $"{nameof(FileSharingSettings)}:{nameof(model.PendingDownloadsPath)}", model.PendingDownloadsPath);
            await _settingsService.SetAsync(ModuleId, $"{nameof(FileSharingSettings)}:{nameof(model.ManifestsPath)}", model.ManifestsPath);
            await _settingsService.SetAsync(ModuleId, $"{nameof(FileSharingSettings)}:{nameof(model.CatalogPath)}", model.CatalogPath);
            await _settingsService.SetAsync(ModuleId, $"{nameof(FileSharingSettings)}:{nameof(model.BlockSizeBytes)}", model.BlockSizeBytes);
            await _settingsService.SetAsync(ModuleId, $"{nameof(FileSharingSettings)}:{nameof(model.SliceSizeBytes)}", model.SliceSizeBytes);
            await _settingsService.SetAsync(ModuleId, $"{nameof(FileSharingSettings)}:{nameof(model.MaxActiveDownloads)}", Math.Max(1, model.MaxActiveDownloads));
            await _settingsService.SetAsync(ModuleId, $"{nameof(FileSharingSettings)}:{nameof(model.MaxUploadSlots)}", Math.Max(1, model.MaxUploadSlots));
            await _settingsService.SetAsync(ModuleId, $"{nameof(FileSharingSettings)}:{nameof(model.MaxUploadSlotsPerPeer)}", Math.Max(1, model.MaxUploadSlotsPerPeer));
            await _settingsService.SetAsync(ModuleId, $"{nameof(FileSharingSettings)}:{nameof(model.MaxUploadRateKBps)}", Math.Max(0, model.MaxUploadRateKBps));
            await _settingsService.SetAsync(ModuleId, $"{nameof(FileSharingSettings)}:{nameof(model.MaxPeerConnectionsPerFile)}", model.MaxPeerConnectionsPerFile);
            await _settingsService.SetAsync(ModuleId, $"{nameof(FileSharingSettings)}:{nameof(model.SliceRequestTimeoutSeconds)}", model.SliceRequestTimeoutSeconds);
            await _settingsService.SetAsync(ModuleId, $"{nameof(FileSharingSettings)}:{nameof(model.SeederDiscoveryTimeoutSeconds)}", model.SeederDiscoveryTimeoutSeconds);
            await _settingsService.SetAsync(ModuleId, $"{nameof(FileSharingSettings)}:{nameof(model.TrackerReannounceIntervalMinutes)}", Math.Max(1, model.TrackerReannounceIntervalMinutes));
            await _settingsService.SetAsync(ModuleId, $"{nameof(FileSharingSettings)}:{nameof(model.SeedCompletedDownloads)}", model.SeedCompletedDownloads);
            await _settingsService.SetAsync(ModuleId, $"{nameof(FileSharingSettings)}:{nameof(model.MaxOutstandingRequestsPerPeer)}", Math.Max(1, model.MaxOutstandingRequestsPerPeer));
            await _settingsService.SetAsync(ModuleId, $"{nameof(FileSharingSettings)}:{nameof(model.UploadQueueLimitPerChannel)}", Math.Max(1, model.UploadQueueLimitPerChannel));
            await _settingsService.SetAsync(ModuleId, $"{nameof(FileSharingSettings)}:{nameof(model.UploadSendBufferHighWaterBytes)}", Math.Max(65536, model.UploadSendBufferHighWaterBytes));
            await _settingsService.SetAsync(ModuleId, $"{nameof(FileSharingSettings)}:{nameof(model.PeerBanAfterBadBlocks)}", Math.Max(1, model.PeerBanAfterBadBlocks));
            await _settingsService.SetAsync(ModuleId, $"{nameof(FileSharingSettings)}:{nameof(model.PeerBanDurationHours)}", Math.Max(1, model.PeerBanDurationHours));

            ViewData["SuccessMessage"] = "File Sharing settings successfully committed.";
        }
        catch (Exception ex)
        {
            ViewData["ErrorMessage"] = $"Failed to persist settings: {ex.Message}";
        }
        finally
        {
            // One reload for the whole save instead of one per key written.
            _settingsService.SetAutoUpdate(true);
            _settingsService.ForceUpdateOptionsMonitor();
        }

        return PartialView("_GeneralSettingsFormPartial", model);
    }

    /// <summary>
    /// Current catalogue record limits, for the admin UI.
    /// </summary>
    [HttpGet]
    public IActionResult GetCatalogSettings()
    {
        var s = _fileSharingSettings.CurrentValue ?? new FileSharingSettings();
        return Json(new
        {
            maxCatalogRecordsPerHost = s.MaxCatalogRecordsPerHost,
            directQueryDefaultPageSize = s.DirectQueryDefaultPageSize,
            catalogRecordStaleAfterHours = s.CatalogRecordStaleAfterHours
        });
    }

    /// <summary>
    /// Updates the catalogue limits. Only the values you pass are changed (each must be at least 1).
    /// Separate from SaveSettings on purpose: that form binds the whole model, so adding these there
    /// would overwrite them with 0 whenever the form doesn't post them.
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveCatalogSettings(int? maxCatalogRecordsPerHost = null, int? directQueryDefaultPageSize = null, int? catalogRecordStaleAfterHours = null)
    {
        if (maxCatalogRecordsPerHost < 1 || directQueryDefaultPageSize < 1 || catalogRecordStaleAfterHours < 1)
        {
            return BadRequest("Every value provided must be at least 1.");
        }

        try
        {
            _settingsService.SetAutoUpdate(false);
            if (maxCatalogRecordsPerHost.HasValue)
                await _settingsService.SetAsync(ModuleId, $"{nameof(FileSharingSettings)}:{nameof(FileSharingSettings.MaxCatalogRecordsPerHost)}", maxCatalogRecordsPerHost.Value);
            if (directQueryDefaultPageSize.HasValue)
                await _settingsService.SetAsync(ModuleId, $"{nameof(FileSharingSettings)}:{nameof(FileSharingSettings.DirectQueryDefaultPageSize)}", directQueryDefaultPageSize.Value);
            if (catalogRecordStaleAfterHours.HasValue)
                await _settingsService.SetAsync(ModuleId, $"{nameof(FileSharingSettings)}:{nameof(FileSharingSettings.CatalogRecordStaleAfterHours)}", catalogRecordStaleAfterHours.Value);

            return Json(new { success = true });
        }
        catch (Exception ex)
        {
            return Json(new { success = false, message = $"Failed to persist settings: {ex.Message}" });
        }
        finally
        {
            // One reload for the whole save instead of one per key written.
            _settingsService.SetAutoUpdate(true);
            _settingsService.ForceUpdateOptionsMonitor();
        }
    }

    /// <summary>
    /// Current access-list rules for description/custom fields received from other hosts, plus the
    /// default action for a field that matches none of them.
    /// </summary>
    [HttpGet]
    public IActionResult GetCustomFieldAccessRules()
    {
        var s = _fileSharingSettings.CurrentValue ?? new FileSharingSettings();
        return Json(new
        {
            rules = s.CustomFieldAccessRules ?? new List<CustomFieldAccessRule>(),
            defaultAction = s.CustomFieldDefaultAction.ToString()
        });
    }

    /// <summary>
    /// Replaces the whole access-list rule set (not a merge) and/or the default action. Rules are
    /// evaluated in the order given - put more specific rules first. Every FieldNamePattern and
    /// ValuePattern is checked for valid regex syntax before anything is saved; if any is invalid,
    /// nothing is changed and the offending pattern is named in the response. rulesJson is a JSON
    /// array of {FieldNamePattern, ValuePattern, Action} - sent as a regular form field (not a raw
    /// JSON body) so the antiforgery token travels the same way as every other form post here.
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveCustomFieldAccessRules(string? rulesJson = null, string? defaultAction = null)
    {
        List<CustomFieldAccessRule>? rules = null;
        if (!string.IsNullOrEmpty(rulesJson))
        {
            try
            {
                rules = System.Text.Json.JsonSerializer.Deserialize<List<CustomFieldAccessRule>>(rulesJson);
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = $"rulesJson is not a valid JSON array of rules: {ex.Message}" });
            }
        }

        FieldRuleAction? parsedDefault = null;
        if (defaultAction != null)
        {
            if (!Enum.TryParse<FieldRuleAction>(defaultAction, ignoreCase: true, out var parsed))
            {
                return Json(new { success = false, message = $"defaultAction must be 'Allow' or 'Deny', got '{defaultAction}'." });
            }
            parsedDefault = parsed;
        }

        if (rules != null)
        {
            foreach (var rule in rules)
            {
                if (string.IsNullOrEmpty(rule.FieldNamePattern))
                {
                    return Json(new { success = false, message = "Every rule needs a non-empty FieldNamePattern." });
                }
                if (!IsValidRegex(rule.FieldNamePattern))
                {
                    return Json(new { success = false, message = $"Invalid regex in FieldNamePattern: '{rule.FieldNamePattern}'." });
                }
                if (!string.IsNullOrEmpty(rule.ValuePattern) && !IsValidRegex(rule.ValuePattern))
                {
                    return Json(new { success = false, message = $"Invalid regex in ValuePattern: '{rule.ValuePattern}'." });
                }
            }
        }

        try
        {
            _settingsService.SetAutoUpdate(false);
            if (rules != null)
                await _settingsService.SetAsync(ModuleId, $"{nameof(FileSharingSettings)}:{nameof(FileSharingSettings.CustomFieldAccessRules)}", rules);
            if (parsedDefault.HasValue)
                await _settingsService.SetAsync(ModuleId, $"{nameof(FileSharingSettings)}:{nameof(FileSharingSettings.CustomFieldDefaultAction)}", parsedDefault.Value.ToString());

            return Json(new { success = true });
        }
        catch (Exception ex)
        {
            return Json(new { success = false, message = $"Failed to persist settings: {ex.Message}" });
        }
        finally
        {
            // One reload for the whole save instead of one per key written.
            _settingsService.SetAutoUpdate(true);
            _settingsService.ForceUpdateOptionsMonitor();
        }
    }

    private static bool IsValidRegex(string pattern)
    {
        try { _ = new System.Text.RegularExpressions.Regex(pattern); return true; }
        catch (ArgumentException) { return false; }
    }
}