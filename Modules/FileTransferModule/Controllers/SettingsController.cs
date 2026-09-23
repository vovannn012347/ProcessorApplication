using Common.Interfaces;

using FileSharingModule.Configuration;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

using ProcessorApplication.Attributes;

namespace FileSharingModule.Controllers;

[ModuleRoute(FileTrModuleEntryPoint.MODULE_ID)]
[Route("/[area]/[controller]/[action]/{id?}")]
[Authorize(Policy = "AdminLocalPolicy", Roles = "Admin")]
[ApiExplorerSettings(IgnoreApi = true)]
public class SettingsController : Controller
{
    public string ModuleId => FileTrModuleEntryPoint.MODULE_ID;

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

        if (Request.Headers["X-Requested-With"] == "XMLHttpRequest")
        {
            return PartialView("_GeneralSettingsPartial", model);
        }

        return View(model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveSettings([FromForm] FileSharingSettings model)
    {
        if (model == null)
        {
            TempData["ErrorMessage"] = "Invalid configuration data received.";
            return RedirectToAction(nameof(Index));
        }

        try
        {
            await _settingsService.SetAsync(ModuleId, $"{nameof(FileSharingSettings)}:{nameof(model.StorageRootPath)}", model.StorageRootPath);
            await _settingsService.SetAsync(ModuleId, $"{nameof(FileSharingSettings)}:{nameof(model.PendingDownloadsPath)}", model.PendingDownloadsPath);
            await _settingsService.SetAsync(ModuleId, $"{nameof(FileSharingSettings)}:{nameof(model.ManifestsPath)}", model.ManifestsPath);
            await _settingsService.SetAsync(ModuleId, $"{nameof(FileSharingSettings)}:{nameof(model.BlockSizeBytes)}", model.BlockSizeBytes);
            await _settingsService.SetAsync(ModuleId, $"{nameof(FileSharingSettings)}:{nameof(model.SliceSizeBytes)}", model.SliceSizeBytes);
            await _settingsService.SetAsync(ModuleId, $"{nameof(FileSharingSettings)}:{nameof(model.MaxActiveUploads)}", model.MaxActiveUploads);
            await _settingsService.SetAsync(ModuleId, $"{nameof(FileSharingSettings)}:{nameof(model.MaxActiveDownloads)}", model.MaxActiveDownloads);
            await _settingsService.SetAsync(ModuleId, $"{nameof(FileSharingSettings)}:{nameof(model.MaxPeerConnectionsPerFile)}", model.MaxPeerConnectionsPerFile);
            await _settingsService.SetAsync(ModuleId, $"{nameof(FileSharingSettings)}:{nameof(model.SliceRequestTimeoutSeconds)}", model.SliceRequestTimeoutSeconds);
            await _settingsService.SetAsync(ModuleId, $"{nameof(FileSharingSettings)}:{nameof(model.SeederDiscoveryTimeoutSeconds)}", model.SeederDiscoveryTimeoutSeconds);

            TempData["SuccessMessage"] = "File Sharing settings successfully committed.";
        }
        catch (Exception ex)
        {
            TempData["ErrorMessage"] = $"Failed to persist settings: {ex.Message}";
        }

        return RedirectToAction(nameof(Index));
    }
}