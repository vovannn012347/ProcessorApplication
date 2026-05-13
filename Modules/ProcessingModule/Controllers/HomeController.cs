using System;
using System.Security.Claims;
using System.Threading;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using Newtonsoft.Json;

using ProcessorApplication.Attributes;
using ProcessorApplication.Services.User;
using ProcessorApplication.Utils;

using ProcessingModule.Configuration;
using ProcessingModule.Database;
using ProcessingModule.Database.Models;
using ProcessingModule.Infrastructure;
using ProcessingModule.Models;
using ProcessingModule.Models.Views;
using ProcessingModule.Services;
using ProcessingModule.Services.Processing;
using ProcessingModule.Services.Sandboxing;
using ProcessingModule.Utils;

namespace ProcessingModule.Controllers;

[Authorize]
[ModuleRoute(ProcessorModule.MODULE_ID)]
[Route("")]
[Route("[controller]/[action]/{id?}")]
public class HomeController : Controller
{
    protected ClaimsPrincipal CurrentUser => User;

    protected readonly ProcessorDbContext _db;
    protected readonly IOptionsMonitor<ProcessorSettings> _settings;
    protected readonly ISandboxProvider _sandboxSelector;
    protected readonly ProcessorApplicationUserManager _userManager;
    protected readonly IProcessingService _processingService;
    protected readonly IScriptIndexer _scriptIndexer;
    protected readonly ProcessingQueue _processingQueue;
    protected readonly TaskControlMonitor _monitor;

    public HomeController(
        IOptionsMonitor<ProcessorSettings> settings,
        ISandboxProvider sandboxSelector,
        ProcessorApplicationUserManager userManager,
        IProcessingService processingService,
        IScriptIndexer scriptIndexer,
        ProcessingQueue queue,
        TaskControlMonitor monitor,
        ProcessorDbContext db)
    {
        _settings = settings;
        _sandboxSelector = sandboxSelector;
        _userManager = userManager;
        _processingService = processingService;
        _scriptIndexer = scriptIndexer;
        _db = db;
        _processingQueue = queue;
        _monitor = monitor;
    }

    [HttpGet]
    public async Task<IActionResult> Queue()
    {
        // Assuming user ID retrieval logic is handled by your identity system
        List<OrchestratedTask> jobs;
        if (CurrentUser.Identity != null && CurrentUser.Identity.IsAuthenticated)
        {
#pragma warning disable CS8600
            string userName = CurrentUser.Identity.Name;
#pragma warning restore CS8600

            var user = await _userManager.FindByNameAsync(userName);
            jobs = await _processingService.GetUserJobsAsync(user.UserName);
        }
        else
        {
            jobs = new List<OrchestratedTask>();
        }

        return Request.IsAjaxRequest() ? PartialView(jobs) : View(jobs);
    }
    [HttpGet]
    public async Task<IActionResult> GetSubJobDetailsAsync(Guid subJobId)
    {
        var details = await _processingService.GetSubJobDetailsAsync(subJobId);
        if (details == null) return NotFound();

        return PartialView("_SubJobDetails", details);
    }

    //public async Task<SubJobDetailsViewModel?> GetSubJobDetailsAsync(Guid subJobId)
    //{
    //    var subJob = await _db.ProcessingJobs
    //        .Include(s => s.ParentJob)
    //        .FirstOrDefaultAsync(s => s.Id == subJobId);

    //    if (subJob == null) return null;

    //    var viewModel = new SubJobDetailsViewModel
    //    {
    //        SubJobId = subJobId,
    //        Status = subJob.Status,
    //        ResultMessage = subJob.ResultMessage
    //    };

    //    string subJobPath = Path.Combine(subJob.ParentJob.PhysicalPathRoot, subJob.StepDirectoryName);

    //    // 1. Load Scalars from direct_input.json
    //    string inputPath = Path.Combine(subJobPath, "direct_input.json");
    //    if (System.IO.File.Exists(inputPath))
    //    {
    //        var json = await System.IO.File.ReadAllTextAsync(inputPath);
    //        var dict = JsonConvert.DeserializeObject<Dictionary<string, object>>(json);
    //        viewModel.Inputs = dict?.Select(kv => new LabelValue(kv.Key, kv.Value?.ToString() ?? "")).ToList() ?? new();
    //    }

    //    // 2. Load Artifacts from file_output.json
    //    string outputPath = Path.Combine(subJobPath, "file_output.json");
    //    if (System.IO.File.Exists(outputPath))
    //    {
    //        var json = await System.IO.File.ReadAllTextAsync(outputPath);
    //        var outputs = JsonConvert.DeserializeObject<Dictionary<string, List<string>>>(json);

    //        foreach (var group in outputs ?? new())
    //        {
    //            foreach (var relPath in group.Value)
    //            {
    //                // Format for FilesController: parentId:subId:path:to:file.jpg
    //                string token = $"{subJob.ParentJobId}:{subJob.Id}:{relPath.Replace(Path.DirectorySeparatorChar, ':')}";

    //                viewModel.Artifacts.Add(new ArtifactViewModel
    //                {
    //                    FileName = Path.GetFileName(relPath),
    //                    Token = token,
    //                    Type = group.Key,
    //                    IsImage = new[] { ".jpg", ".jpeg", ".png" }.Contains(Path.GetExtension(relPath).ToLower())
    //                });
    //            }
    //        }
    //    }
    //    return viewModel;
    //}


    public IActionResult ScriptList()
    {
        var scripts = _scriptIndexer.GetAvailableScripts();

        if (Request.IsAjaxRequest())
        {
            return PartialView(scripts);
        }

        return View(scripts);
    }

    [HttpGet]
    public async Task<IActionResult> GetScriptDetails(string scriptIdentifier)
    {
        var scriptIndex = await _db.Scripts
            .FirstOrDefaultAsync(s => s.ScriptIdentifier == scriptIdentifier);

        if (scriptIndex == null) return NotFound();

        string baseSourcePath = _settings.CurrentValue.ScriptSourcePath;
        string manifestPath = Path.Combine(baseSourcePath, scriptIndex.ManifestDirectoryPath, MicsConstants.ScriptManifestFile);

        if (!System.IO.File.Exists(manifestPath))
            return PartialView("_ScriptDetails", new ScriptDetailsViewModel());

        try
        {
            var jsonContent = await System.IO.File.ReadAllTextAsync(manifestPath);
            var manifest = JsonConvert.DeserializeObject<ScriptManifest>(jsonContent);

            if (manifest == null)
                return PartialView("_ScriptDetails", new ScriptDetailsViewModel());

            // Resolve which localization file to use based on browser headers
            string localeFile = ResolveLocalizationFilename(manifest);
            Dictionary<string, string> localizationDict = await LoadLocalizationAsync(scriptIndex, localeFile);

            // Build mapping: Label (from Input) -> Localization Key (from Orchestration)
            var inputKeyMap = manifest.OrchestrationInputs
                .Where(x => !string.IsNullOrEmpty(x.LabelParam))
                .ToDictionary(x => x.LabelParam, x => x.LocalizationLabel);

            var outputKeyMap = manifest.OrchestrationOutputs
                .Where(x => !string.IsNullOrEmpty(x.LabelParam))
                .ToDictionary(x => x.LabelParam, x => x.LocalizationLabel);

            string GetLocalizedLabel(string rawLabel, Dictionary<string, string> keyMap)
            {
                // Find the key in the orchestration mapping
                if (keyMap.TryGetValue(rawLabel, out var locKey))
                {
                    // Find the translation for that key in the locale file
                    if (localizationDict.TryGetValue(locKey, out var translated))
                        return translated;

                    return locKey; // Fallback to key name if translation missing
                }
                return rawLabel; // Fallback to raw label if orchestration mapping missing
            }

            var systemTypes = InOutType.SourceInput;

            var viewModel = new ScriptDetailsViewModel
            {
                Inputs = manifest.Inputs
                    .Where(i => !systemTypes.Contains(i.Type))
                    .Select(i => new ParameterDetailViewModel
                    {
                        DisplayName = GetLocalizedLabel(i.Label, inputKeyMap),
                        Type = i.Type
                    }).ToList(),

                Outputs = manifest.Outputs
                    .Select(o => new ParameterDetailViewModel
                    {
                        DisplayName = GetLocalizedLabel(o.Label, outputKeyMap),
                        Type = o.Type
                    }).ToList()
            };

            return PartialView("_ScriptDetails", viewModel);
        }
        catch (Exception)
        {
            return StatusCode(500, "Error processing script manifest");
        }
    }

    /// <summary>
    /// prepares a page with be script inputs
    /// inputs may be merged if their type and label is equal 
    /// </summary>
    /// <param name="selectedScripts">comma-delimited list of script ids to launch</param>
    /// <returns></returns>
    [HttpPost]
    public async Task<IActionResult> PrepareJob(string selectedScripts)
    {
        if (string.IsNullOrEmpty(selectedScripts)) return RedirectToAction("Queue");

        var ids = selectedScripts.Split(',');
        var scripts = await _db.Scripts
            .Where(s => ids.Contains(s.ScriptIdentifier))
            .ToListAsync();

        var inputGroups = new Dictionary<string, GroupedInputViewModel>();

        foreach (var script in scripts)
        {
            ScriptManifest? manifest = await LoadManifestAsync(script);
            if (manifest == null) continue;

            string localeFile = ResolveLocalizationFilename(manifest);
            var localization = await LoadLocalizationAsync(script, localeFile);

            var orchestratorMap = manifest.OrchestrationInputs
                .Where(x => !string.IsNullOrEmpty(x.LabelParam))
                .ToDictionary(x => x.LabelParam, x => x.LocalizationLabel);

            var systemTypes = InOutType.SourceInput;

            foreach (var input in manifest.Inputs)
            {
                if (systemTypes.Contains(input.Type)) continue;

                string key = $"{input.Label}|{input.Type}";

                string description = input.Label;
                if (orchestratorMap.TryGetValue(input.Label, out var locKey))
                {
                    if (!localization.TryGetValue(locKey, out description))
                        description = locKey;
                }

                var meta = new ScriptInputMetadata
                {
                    ScriptLabel = script.ScriptLabel,
                    LocalizedDescription = description
                };

                if (!inputGroups.TryGetValue(key, out var group))
                {
                    group = new GroupedInputViewModel { Label = input.Label, Type = input.Type };
                    inputGroups[key] = group;
                }
                group.Sources.Add(meta);
            }
        }

        return View(new PrepareJobViewModel
        {
            ScriptIds = selectedScripts,
            SelectedScripts = scripts,
            GroupedInputs = inputGroups.Values.ToList()
        });
    }

    /// <summary>
    /// prepares script for processing
    /// creates task manifest
    /// creates sub-job manifests
    /// downloads and places files correctly
    /// tehre is no full orchestration stuff... yet
    /// </summary>
    /// <param name="scriptIds">comma-delimited list of script ids to launch</param>
    /// <param name="values">entered values</param>
    /// <returns></returns>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> FinalizeStart(string scriptIds, Dictionary<string, string> values)
    {
        if (string.IsNullOrEmpty(scriptIds)) return BadRequest("No scripts selected.");

        // 1. Root and Manifest Directory Initialization
        var jobId = Guid.NewGuid();
        var jobRoot = Path.Combine(_settings.CurrentValue.ResultsOutputPath, jobId.ToString());
        var manifestsDir = Path.Combine(jobRoot, MicsConstants.OrchestrationDirectory);
        var globalInputsPath = Path.Combine(jobRoot, MicsConstants.FileInputsDirectory);

        Directory.CreateDirectory(jobRoot);
        Directory.CreateDirectory(manifestsDir);
        Directory.CreateDirectory(globalInputsPath);

        var task = new OrchestratedTask
        {
            Id = jobId,
            Status = TaskStatusKeyword.Pending,
            CreatedTime = DateTime.UtcNow,
            InitiatorUserId = User.Identity?.Name ?? "system",
            PhysicalPathRoot = jobRoot
        };

        var orchestrationManifest = new OrchestrationManifest
        {
            JobId = jobId,
            Status = TaskStatusKeyword.Pending
        };

        var ids = scriptIds.Split(',');

        //files ARE shared
        var sharedFilesDIrectories = new Dictionary<string, string>();
        // 2. Loop through Scripts
        for (int i = 0; i < ids.Length; i++)
        {
            var scriptIndex = await _db.Scripts.FirstOrDefaultAsync(s => s.ScriptIdentifier == ids[i]);
            if (scriptIndex == null) continue;

            ScriptManifest? scriptManifest = await LoadManifestAsync(scriptIndex);
            if (scriptManifest == null) continue;

            var subJobId = Guid.NewGuid();
            var stepDirName = $"step_{i + 1}_{subJobId}";
            string subJobDir = Path.Combine(jobRoot, stepDirName);
            Directory.CreateDirectory(subJobDir);

            var subJob = new OrchestratedTaskSubJob
            {
                Id = subJobId,
                ParentJobId = jobId,
                ScriptId = ids[i],
                Sequence = i + 1,
                Status = TaskStatusKeyword.Pending,
                StepDirectoryName = stepDirName
            };

            var directInputs = new Dictionary<string, object>();
            var inputsBase = new Dictionary<string, string>();
            var outputsBase = new Dictionary<string, string>();
            var stepInputs = new Dictionary<string, string>();
            var stepOutputs = new Dictionary<string, string>();

            // 3. Process Inputs (Files/Folders & Scalars)
            foreach (var input in scriptManifest.Inputs)
            {
                string label = input.Label;
                string type = input.Type;
                string key = $"{label}|{type}";

                //files are relative to the root folder
                if (type == InOutType.InOutFile || type == InOutType.InOutFileMultiple)
                {
                    if (!sharedFilesDIrectories.TryGetValue(key, out string fileDirPathValue))
                    {
                        //if we have not encountered this yet
                        string paramDirName = Guid.NewGuid().ToString();
                        string paramFullPath = Path.Combine(globalInputsPath, paramDirName);
                        Directory.CreateDirectory(paramFullPath);

                        var prefix = type == InOutType.InOutFile ? "files_" : "folders_";
                        var uploadedFiles = Request.Form.Files.GetFiles($"{prefix}{key}");

                        //relative to job root
                        fileDirPathValue = $"./{MicsConstants.FileInputsDirectory}/{paramDirName}";
                        string finalPath = paramFullPath;

                        if (type == InOutType.InOutFile && uploadedFiles.Any())
                        {
                            var file = uploadedFiles.First();
                            string fileName = Path.GetFileName(file.FileName);
                            string targetFile = Path.Combine(paramFullPath, fileName);
                            using (var stream = new FileStream(targetFile, FileMode.Create)) await file.CopyToAsync(stream);

                            finalPath = targetFile;
                            fileDirPathValue += $"/{fileName}";
                        }
                        else
                        {
                            foreach (var file in uploadedFiles)
                            {
                                string targetPath = Path.Combine(paramFullPath, Path.GetFileName(file.FileName));
                                using var stream = new FileStream(targetPath, FileMode.Create);
                                await file.CopyToAsync(stream);
                            }
                        }
                    }

                    inputsBase[label] = fileDirPathValue;
                    stepInputs[label] = fileDirPathValue;
                }
                else 
                if (InOutType.ScalarInput.Contains(type.ToLower()) &&
                    values.TryGetValue(key, out var userValue))
                {
                    directInputs[label] = type switch
                    {
                        InOutType.Boolean => bool.TryParse(userValue, out bool b) && b,
                        InOutType.Integer => int.TryParse(userValue, out int iv) ? iv : 0,
                        InOutType.Decimal => decimal.TryParse(userValue, out decimal dv) ? dv : 0m,
                        _ => userValue
                    };

                    string relScalarPath = $"./{subJob.StepDirectoryName}/{MicsConstants.DirectInputFile}";
                    inputsBase[label] = relScalarPath;
                    stepInputs[label] = relScalarPath;
                }
            }

            // 4. Populate Outputs
            foreach (var output in scriptManifest.Outputs)
            {
                // Default: Relative to folder_base, includes stepDirName
                // yes, we directly point scripts where needed
                // Overrides in orchestration manifest are WIP for when proper orchestration is implemented

                string type = output.Type;

                //files are relative to the root folder
                //and point to the step folder currently
                if (InOutType.FileInput.Contains(type.ToLower()))
                {
                    if (string.IsNullOrEmpty(output.DiskPath))
                    {
                        outputsBase[output.Label] =
                            $"./{subJob.StepDirectoryName}/{Guid.NewGuid().ToString()}";
                    }
                    else
                    {
                        outputsBase[output.Label] =
                            $"./{subJob.StepDirectoryName}/{output.DiskPath}";
                    }
                }
                else
                //scalars are outputted directly into step folder direct_output.json
                if (InOutType.ScalarInput.Contains(type.ToLower()))
                {
                    string relScalarPath = $"./{subJob.StepDirectoryName}/{MicsConstants.DirectOutputFile}";
                }
            }

            // 5. Save Artifacts
            await System.IO.File.WriteAllTextAsync(
                Path.Combine(subJobDir, MicsConstants.DirectInputFile),
                JsonConvert.SerializeObject(directInputs, Formatting.Indented));

            var subJobManifest = new ProcessingManifest
            {
                RunScript = subJob.ScriptId,
                Status = TaskStatusKeyword.Pending,
                FolderBase = $"./{subJob.StepDirectoryName}",
                InputsFoldersBase = inputsBase,
                OutputFoldersBase = outputsBase
            };

            // Sub-job manifests go to processing_manifests/
            await System.IO.File.WriteAllTextAsync(
                Path.Combine(manifestsDir, $"{subJobId}.json"),
                JsonConvert.SerializeObject(subJobManifest, Formatting.Indented));

            orchestrationManifest.Steps.Add(new OrchestrationStep
            {
                Sequence = subJob.Sequence,
                ProcessingId = subJobId,
                ScriptId = subJob.ScriptId,
                Inputs = stepInputs,
                Outputs = stepOutputs // Contains overrides if any
            });

            task.SubJobs.Add(subJob);
        }

        // 6. Save Orchestration Manifest to processing_manifests/
        await System.IO.File.WriteAllTextAsync(
            Path.Combine(manifestsDir, MicsConstants.OrchestrationManifestFile),
            JsonConvert.SerializeObject(orchestrationManifest, Formatting.Indented));

        // 7. Persist and Queue
        _db.Jobs.Add(task);
        await _db.SaveChangesAsync();

        // STREAMLINED: Call the service-level failsafe instead of local method
        await _processingService.CreateJournalAsync(task);

        await _processingQueue.EnqueueAsync(task.Id);
        return RedirectToAction("Queue");
    }

    //private Dictionary<string, string> GetOutputMappingFromManifest(ScriptManifest manifest, string stepDirectoryName)
    //{
    //    var outputMapping = new Dictionary<string, string>();
    //    if (manifest.Outputs == null) return outputMapping;

    //    foreach (var output in manifest.Outputs)
    //    {
    //        string label = output.Label;
    //        string type = output.Type;
    //        string diskPath = output.DiskPath ?? "";

    //        if (new[] { "integer", "decimal", "boolean", "string" }.Contains(type.ToLower()))
    //            continue;

    //        // Ensure relative path logic remains cross-platform
    //        outputMapping[label] = $"{stepDirectoryName}/{diskPath}".TrimEnd('/');
    //    }

    //    return outputMapping;
    //}

    //private async Task CreateJobJournalAsync(OrchestratedTask task, Dictionary<string, string> originalInputs)
    //{
    //    var journalDir = Path.Combine(_settings.CurrentValue.ResultsOutputPath, MicsConstants.JournalsDirectory);
    //    Directory.CreateDirectory(journalDir);

    //    var journal = new
    //    {
    //        TaskId = task.Id,
    //        Root = task.PhysicalPathRoot,
    //        SubJobs = task.SubJobs.OrderBy(s => s.Sequence).Select(s => s.Id).ToList(),
    //        Timestamp = DateTime.UtcNow
    //    };

    //    string journalPath = Path.Combine(journalDir, $"{task.Id}.{FileExtensions.Journal}");
    //    await System.IO.File.WriteAllTextAsync(journalPath, JsonConvert.SerializeObject(journal, Formatting.Indented));
    //}

    private async Task<Dictionary<string, string>> LoadLocalizationAsync(ScriptIndex script, string localizationFile)
    {
        var locDir = Path.Combine(_settings.CurrentValue.ScriptSourcePath, script.ManifestDirectoryPath, MicsConstants.LocalizationDirectory);
        var locPath = Path.Combine(locDir, localizationFile);

        if (!System.IO.File.Exists(locPath)) return new Dictionary<string, string>();

        var json = await System.IO.File.ReadAllTextAsync(locPath);
        return JsonConvert.DeserializeObject<Dictionary<string, string>>(json) ?? new Dictionary<string, string>();
    }

    private string ResolveLocalizationFilename(ScriptManifest manifest, string defaultLocale = "en")
    {
        var localization = manifest.Localization;
        if (localization == null || localization.Count == 0) return "";

        var acceptLanguages = Request.GetTypedHeaders().AcceptLanguage?
            .OrderByDescending(l => l.Quality ?? 1.0);

        if (acceptLanguages != null)
        {
            foreach (var lang in acceptLanguages)
            {
                var culture = lang.Value.Value;
                if (string.IsNullOrEmpty(culture)) continue;

                if (localization.TryGetValue(culture, out var file)) return file;

                var neutral = culture.Split('-')[0];
                if (localization.TryGetValue(neutral, out file)) return file;
            }
        }

        return localization.TryGetValue(defaultLocale, out var fallback) ? fallback : localization.Values.FirstOrDefault() ?? "";
    }
    private async Task<ScriptManifest?> LoadManifestAsync(ScriptIndex scriptIndex)
    {
        string baseSourcePath = _settings.CurrentValue.ScriptSourcePath;
        // ManifestDirectoryPath is relative to the global ScriptSourcePath
        string manifestPath = Path.Combine(baseSourcePath, 
            scriptIndex.ManifestDirectoryPath, MicsConstants.ScriptManifestFile);

        if (!System.IO.File.Exists(manifestPath)) return null;

        string jsonContent = await System.IO.File.ReadAllTextAsync(manifestPath);
        return JsonConvert.DeserializeObject<ScriptManifest>(jsonContent);
    }

    [HttpPost]
    public async Task<IActionResult> StopJob(Guid id)
    {
        //[cite_start]// Permission check: ensure user owns the task before signaling 
        bool success = await _processingService.StopJobAsync(id);
        return success ? Ok() : NotFound();
    }

    [HttpPost]
    public async Task<IActionResult> PauseJob(Guid id)
    {
        bool success = await _processingService.PauseJobAsync(id);
        return success ? Ok() : NotFound();
    }

    [HttpPost]
    public async Task<IActionResult> ResumeJob(Guid id)
    {
        bool success = await _processingService.ResumeJobAsync(id);
        return success ? Ok() : NotFound();
    }

    [HttpPost]
    public async Task<IActionResult> RestartJob(Guid id)
    {
        bool success = await _processingService.RestartJobAsync(id);
        return success ? Ok() : NotFound();
    }
}