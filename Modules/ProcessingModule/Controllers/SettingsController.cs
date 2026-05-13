using System.Text;

using Common.Interfaces;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

using ProcessorApplication.Attributes;
using ProcessorApplication.Utils;

using ProcessingModule.Configuration;
using ProcessingModule.Infrastructure;
using ProcessingModule.Models.Views;
using ProcessingModule.Services.Runtime;

namespace ProcessingModule.Controllers;

[Authorize(Policy = "AdminLocalPolicy")]
[ModuleRoute(ProcessorModule.MODULE_ID)]
[Route("[controller]/[action]/{id?}")]
public class SettingsController : Controller
{
    public string ModuleId => ProcessorModule.MODULE_ID;

    private readonly ISettingService _settingsService;
    private readonly IOsUserManagementService _osUserService;
    private readonly IOptionsMonitor<ProcessorSettings> _general;
    private readonly IOptionsMonitor<PythonProcessingSettings> _python;
    private readonly IOptionsMonitor<NoneSandboxSettings> _noneSandbox;
    private readonly IOptionsMonitor<OsSandboxSettings> _osSandbox;
    private readonly IOptionsMonitor<DockerSandboxSettings> _dockerSandbox;

    public SettingsController(
        ISettingService settingsService,
        IOsUserManagementService osUserService,
        IOptionsMonitor<ProcessorSettings> general,
        IOptionsMonitor<PythonProcessingSettings> python,
        IOptionsMonitor<NoneSandboxSettings> none,
        IOptionsMonitor<OsSandboxSettings> osSandbox,
        IOptionsMonitor<DockerSandboxSettings> dockerSandbox)
    {
        _osUserService = osUserService;
        _settingsService = settingsService;
        _general = general;
        _python = python;
        _noneSandbox = none;
        _osSandbox = osSandbox;
        _dockerSandbox = dockerSandbox;
    }

    public IActionResult Index()
    {
        var model = new ProcessorSettingsViewModel
        {
            General = _general.CurrentValue,
            Python = _python.CurrentValue,
            //None = _none.CurrentValue,
            OsSandbox = _osSandbox.CurrentValue,
            DockerSandbox = _dockerSandbox.CurrentValue
        };

        if (Request.IsAjaxRequest())
        {
            return PartialView(model);
        }

        return View(model);
    }

    [HttpPost]
    public async Task<IActionResult> SaveProcessor(ProcessorSettingsViewModel model)
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
            //await SaveDockerSandbox(model.DockerSandbox);

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
        //await _settingsService.SetAsync(ModuleId, $"{nameof(OsSandboxSettings)}:{nameof(model.AutoCreateProcessingUser)}", model.AutoCreateProcessingUser);
        await _settingsService.SetAsync(ModuleId, $"{nameof(OsSandboxSettings)}:{nameof(model.GroupName)}", model.GroupName);
        //await _settingsService.SetAsync(ModuleId, $"{nameof(OsSandboxSettings)}:{nameof(model.HomeDirectory)}", model.HomeDirectory);
        //await _settingsService.SetAsync(ModuleId, $"{nameof(OsSandboxSettings)}:{nameof(model.HomeDirectory)}", model.HomeDirectory);
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

    [HttpGet]
    public async Task StreamOsProvisioning()
    {

        Response.Headers.Add("Content-Type", "text/event-stream");
        Response.Headers.Add("Cache-Control", "no-cache");
        Response.Headers.Add("Connection", "keep-alive");
        var ct = HttpContext.RequestAborted;

        try
        {
            await _osUserService.ProvisionUserAsync(async (msg) =>
            {
                if (ct.IsCancellationRequested) return;
                await Response.Body.WriteAsync(Encoding.UTF8.GetBytes($"data: {msg}\n\n"), ct);
                await Response.Body.FlushAsync(ct);
            }, ct);

            await Response.Body.WriteAsync(Encoding.UTF8.GetBytes("data: [DONE]\n\n"), ct);

        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            var errorData = $"data: [CRITICAL] {ex.Message}\n\n";
            await Response.Body.WriteAsync(Encoding.UTF8.GetBytes(errorData));
        }
        
    }

    [HttpGet]
    public async Task StreamOsStatusCheck()
    {
        Response.Headers.Add("Content-Type", "text/event-stream");
        Response.Headers.Add("Cache-Control", "no-cache");
        Response.Headers.Add("Connection", "keep-alive");
        var ct = HttpContext.RequestAborted;

        try
        {
            await _osUserService.CheckStatusAsync(async (msg) =>
            {
                if (ct.IsCancellationRequested) return;
                await Response.Body.WriteAsync(Encoding.UTF8.GetBytes($"data: {msg}\n\n"), ct);
                await Response.Body.FlushAsync(ct);
            }, ct);

            await Response.Body.WriteAsync(Encoding.UTF8.GetBytes("data: [DONE]\n\n"), ct);

        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            var errorData = $"data: [CRITICAL] {ex.Message}\n\n";
            await Response.Body.WriteAsync(Encoding.UTF8.GetBytes(errorData));
        }
    }
}