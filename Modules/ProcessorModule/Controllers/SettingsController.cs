using Common.Interfaces;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

using ProcessorApplication.Attributes;
using ProcessorApplication.Utils;

using ProcessorModule.Configuration;
using ProcessorModule.Models.Views;
using ProcessorModule.Services.Runtime;

namespace ProcessorModule.Controllers;

[Authorize(Policy = "AdminLocalPolicy")]
[ModuleRoute("Processor")]
[Route("[controller]/[action]/{id?}")]
public class SettingsController : Controller
{
    public string ModuleId => ProcessorModule.MODULE_ID;

    private readonly ISettingService _settingsService;
    private readonly IOptionsMonitor<ProcessorSettings> _general;
    private readonly IOptionsMonitor<PythonProcessingSettings> _python;
    private readonly IOptionsMonitor<NoneSandboxSettings> _none;
    private readonly IOptionsMonitor<OsSandboxSettings> _os;
    private readonly IOptionsMonitor<DockerSandboxSettings> _docker;

    public SettingsController(
        ISettingService settingsService,
        IOptionsMonitor<ProcessorSettings> general,
        IOptionsMonitor<PythonProcessingSettings> python,
        IOptionsMonitor<NoneSandboxSettings> none,
        IOptionsMonitor<OsSandboxSettings> os,
        IOptionsMonitor<DockerSandboxSettings> docker)
    {
        _settingsService = settingsService;
        _general = general;
        _python = python;
        _none = none;
        _os = os;
        _docker = docker;
    }

    public IActionResult Index()
    {
        var model = new ProcessorSettingsViewModel
        {
            General = _general.CurrentValue,
            Python = _python.CurrentValue,
            //None = _none.CurrentValue,
            OsSandbox = _os.CurrentValue,
            DockerSandbox = _docker.CurrentValue
        };

        if (Request.IsAjaxRequest())
        {
            return PartialView(model);
        }

        return View(model);
    }

    [HttpPost]
    public async Task<IActionResult> Save(ProcessorSettingsViewModel model)
    {

        if (model.General.SandboxingType != SandboxType.OSUser)
        {
            foreach (var key in ModelState.Keys.Where(k => k.StartsWith(nameof(model.OsSandbox))).ToList())
            {
                ModelState.Remove(key);
            }
        }

        if (model.General.SandboxingType != SandboxType.Docker)
        {
            foreach (var key in ModelState.Keys.Where(k => k.StartsWith(nameof(model.DockerSandbox))).ToList())
            {
                ModelState.Remove(key);
            }
        }


        if (!ModelState.IsValid)
        {
            if (Request.IsAjaxRequest())
            {
                return PartialView("Index", model);
            }

            return View("Index", model);
        }

        try
        {
            _settingsService.SetAutoUpdate(false);

            await SaveGeneral(model.General);
            await SavePython(model.Python);

            await SaveOsSandbox(model.OsSandbox);
            await SaveDockerSandbox(model.DockerSandbox);

            _settingsService.SetAutoUpdate(true);
            _settingsService.ForceUpdateOptionsMonitor();

            TempData["SuccessMessage"] = "Processor settings updated successfully.";
        }
        catch (Exception ex)
        {
            ModelState.AddModelError("", "Error saving settings: " + ex.Message);
        }

        if (Request.IsAjaxRequest())
        {
            return PartialView("Index", model);
        }

        return View("Index", model);
    }

    private async Task SaveGeneral(ProcessorSettings model)
    {
        await _settingsService.SetAsync(ModuleId, $"{nameof(ProcessorSettings)}:{nameof(model.ScriptSourcePath)}", model.ScriptSourcePath);
        await _settingsService.SetAsync(ModuleId, $"{nameof(ProcessorSettings)}:{nameof(model.ResultsOutputPath)}", model.ResultsOutputPath);
        await _settingsService.SetAsync(ModuleId, $"{nameof(ProcessorSettings)}:{nameof(model.MaxConcurrentJobs)}", model.MaxConcurrentJobs);
        await _settingsService.SetAsync(ModuleId, $"{nameof(ProcessorSettings)}:{nameof(model.JobTimeoutMinutes)}", model.JobTimeoutMinutes);
        await _settingsService.SetAsync(ModuleId, $"{nameof(ProcessorSettings)}:{nameof(model.ProcessingType)}", model.ProcessingType);
        await _settingsService.SetAsync(ModuleId, $"{nameof(ProcessorSettings)}:{nameof(model.SandboxingType)}", model.SandboxingType);
        await _settingsService.SetAsync(ModuleId, $"{nameof(ProcessorSettings)}:{nameof(model.RequireSandboxing)}", model.RequireSandboxing);
        await _settingsService.SetAsync(ModuleId, $"{nameof(ProcessorSettings)}:{nameof(model.ValidateSandboxOnStartup)}", model.ValidateSandboxOnStartup);
    }

    private async Task SavePython(PythonProcessingSettings model)
    {
        await _settingsService.SetAsync(ModuleId, $"{nameof(PythonProcessingSettings)}:{nameof(model.PythonExecutablePath)}", model.PythonExecutablePath);
        await _settingsService.SetAsync(ModuleId, $"{nameof(PythonProcessingSettings)}:{nameof(model.LogStdout)}", model.LogStdout);
    }

    private async Task SaveOsSandbox(OsSandboxSettings model)
    { 
        await _settingsService.SetAsync(ModuleId, $"{nameof(OsSandboxSettings)}:{nameof(model.AutoCreateProcessingUser)}", model.AutoCreateProcessingUser);
        await _settingsService.SetAsync(ModuleId, $"{nameof(OsSandboxSettings)}:{nameof(model.GroupName)}", model.GroupName);
        await _settingsService.SetAsync(ModuleId, $"{nameof(OsSandboxSettings)}:{nameof(model.HomeDirectory)}", model.HomeDirectory);
        await _settingsService.SetAsync(ModuleId, $"{nameof(OsSandboxSettings)}:{nameof(model.Shell)}", model.Shell);
        await _settingsService.SetAsync(ModuleId, $"{nameof(OsSandboxSettings)}:{nameof(model.UserName)}", model.UserName);
    }

    private async Task SaveDockerSandbox(DockerSandboxSettings model)
    {
        await _settingsService.SetAsync(ModuleId, $"{nameof(DockerSandboxSettings)}:{nameof(model.ContainerTimeoutSeconds)}", model.ContainerTimeoutSeconds);
        await _settingsService.SetAsync(ModuleId, $"{nameof(DockerSandboxSettings)}:{nameof(model.CpuShares)}", model.CpuShares);
        await _settingsService.SetAsync(ModuleId, $"{nameof(DockerSandboxSettings)}:{nameof(model.ImageName)}", model.ImageName);
        await _settingsService.SetAsync(ModuleId, $"{nameof(DockerSandboxSettings)}:{nameof(model.MemoryLimitMb)}", model.MemoryLimitMb);
        await _settingsService.SetAsync(ModuleId, $"{nameof(DockerSandboxSettings)}:{nameof(model.NetworkMode)}", model.NetworkMode);
        await _settingsService.SetAsync(ModuleId, $"{nameof(DockerSandboxSettings)}:{nameof(model.ReadOnlyRootFs)}", model.ReadOnlyRootFs);
        await _settingsService.SetAsync(ModuleId, $"{nameof(DockerSandboxSettings)}:{nameof(model.User)}", model.User);
        await _settingsService.SetAsync(ModuleId, $"{nameof(DockerSandboxSettings)}:{nameof(model.Volumes)}", model.Volumes);
    }
}