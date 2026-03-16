using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

using ProcessorModule.Configuration;
using ProcessorModule.Database;
using ProcessorModule.Database.Models;
using ProcessorModule.Infrastructure;
using ProcessorModule.Models;
using ProcessorModule.Models.Views;

namespace ProcessorModule.Services.Processing;

public class ProcessingService : IProcessingService
{
    private readonly ProcessorDbContext _db;
    private readonly ISandboxProvider _sandboxSelector;
    private readonly TaskControlMonitor _monitor;
    private readonly IOptionsMonitor<ProcessorSettings> _settings;
    protected readonly ProcessingQueue _processingQueue; 
    
    private static bool _isSystemHalted = false;

    public ProcessingService(
        ProcessorDbContext db,
        ISandboxProvider sandboxProvider,
        TaskControlMonitor monitor,
        ProcessingQueue queue,
        IOptionsMonitor<ProcessorSettings> settings)
    {
        _db = db;
        _sandboxSelector = sandboxProvider;
        _monitor = monitor;
        _settings = settings;
        _processingQueue = queue;
    }

    public async Task<List<OrchestratedTask>> GetUserJobsAsync(string userId)
    {
        return await _db.Jobs
                .Where(j => j.InitiatorUserId == userId)
                .OrderByDescending(j => j.CreatedTime)
                .Include(j => j.SubJobs)
                .ToListAsync();
    }

    public async Task ProcessTaskSequenceAsync(Guid taskId, CancellationToken ct)
    {
        if (_isSystemHalted) return;

        var task = await _db.Jobs.Include(j => j.SubJobs).FirstOrDefaultAsync(j => j.Id == taskId);
        if (task == null ||
            task.Status == TaskStatusKeyword.Stopped ||
            task.Status == TaskStatusKeyword.Paused) return;

        task.Status = TaskStatusKeyword.Running;
        await _db.SaveChangesAsync();

        var sandbox = _sandboxSelector.GetActiveProcessor();

        // 2. Execution Loop
        foreach (var subJob in task.SubJobs.OrderBy(s => s.Sequence))
        {
            // Skip already finished steps (Resume logic)
            if (subJob.Status == TaskStatusKeyword.Complete) continue;

            // Check for manual user intervention (Stop/Pause)
            await _db.Entry(task).ReloadAsync();
            if (ct.IsCancellationRequested ||
                task.Status == TaskStatusKeyword.Stopped ||
                task.Status == TaskStatusKeyword.Paused) break;

            subJob.Status = TaskStatusKeyword.Running;
            await _db.SaveChangesAsync();

            try
            {
                // Physical execution via standalone executor
                await sandbox.ExecuteJobAsync(subJob.Id);

                // Cleanup artifacts not declared in file_output.json
                await PurgeUnmappedFilesAsync(subJob, task.PhysicalPathRoot);

                subJob.Status = TaskStatusKeyword.Complete;
                subJob.CompletedTime = DateTime.UtcNow;
            }
            catch (FileNotFoundException ex)
            {
                // Infrastructure Failure: Lock threads and mark as ServerError
                _isSystemHalted = true;
                subJob.Status = TaskStatusKeyword.ServerError;
                subJob.ResultMessage = $"[SERVER ERROR] {ex.Message}";
                task.Status = TaskStatusKeyword.ServerError;
                await _db.SaveChangesAsync();
                return;
            }
            catch (Exception ex)
            {
                // Execution Failure: Mark as Error and halt the sequence
                subJob.Status = TaskStatusKeyword.Error;
                subJob.ResultMessage = ex.Message;
                task.Status = TaskStatusKeyword.Error;
                await _db.SaveChangesAsync();
                return;
            }

            await _db.SaveChangesAsync();
        }

        // 3. Finalize Logic
        if (task.Status != TaskStatusKeyword.Stopped && task.Status != TaskStatusKeyword.Paused)
        {
            // Set overall status based on whether all steps completed
            bool allFinished = task.SubJobs.All(s => s.Status == TaskStatusKeyword.Complete);
            task.Status = allFinished ? TaskStatusKeyword.Complete : TaskStatusKeyword.Error;
            task.CompletedTime = DateTime.UtcNow;

            // SUCCESS/TERMINAL FAILSAFE: Delete the orchestration journal
            // This stops the background service from trying to recover this job on next restart.
            DeleteJournal(taskId);

            // Optional: Perform job-level cleanup of the central 'inputs' folder here 
            // if your settings require visual preservation only for 'Complete' states.
        }

        await _db.SaveChangesAsync();

        // 4. Resource Cleanup
        _monitor.Remove(taskId);
    }

    private async Task PurgeUnmappedFilesAsync(OrchestratedTaskSubJob subJob, string rootPath)
    {
        string subJobDir = Path.Combine(rootPath, subJob.StepDirectoryName);
        string fileOutputJson = Path.Combine(subJobDir, MicsConstants.FileOutputFile);

        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            MicsConstants.FileOutputFile,
            MicsConstants.DirectOutputFile,
            MicsConstants.ScriptSummaryFile,
            MicsConstants.DirectInputFile
        };

        if (File.Exists(fileOutputJson))
        {
            var json = await File.ReadAllTextAsync(fileOutputJson);
            var outputs = JsonConvert.DeserializeObject<Dictionary<string, List<string>>>(json);
            if (outputs != null)
            {
                foreach (var list in outputs.Values)
                {
                    foreach (var relPath in list)
                        allowed.Add(Path.GetFileName(relPath));
                }
            }
        }

        var di = new DirectoryInfo(subJobDir);
        foreach (var file in di.GetFiles("*", SearchOption.AllDirectories))
        {
            if (!allowed.Contains(file.Name)) file.Delete();
        }
    }

    /*
    public async Task<SubJobDetailsViewModel?> GetSubJobDetailsAsync(Guid subJobId)
    {
        var subJob = await _db.ProcessingJobs.Include(s => s.ParentJob).FirstOrDefaultAsync(s => s.Id == subJobId);
        if (subJob == null) return null;

        var vm = new SubJobDetailsViewModel { SubJobId = subJobId, Status = subJob.Status, ResultMessage = subJob.ResultMessage };
        string path = Path.Combine(subJob.ParentJob.PhysicalPathRoot, subJob.StepDirectoryName);

        string inputPath = Path.Combine(path, MicsConstants.DirectInputFile);
        if (File.Exists(inputPath))
        {
            var json = await File.ReadAllTextAsync(inputPath);
            var dict = JsonConvert.DeserializeObject<Dictionary<string, object>>(json);
            vm.Inputs = dict?.Select(kv => new LabelValue(kv.Key, kv.Value?.ToString() ?? "")).ToList() ?? new();
        }

        string outputPath = Path.Combine(path, MicsConstants.FileOutputFile);
        if (File.Exists(outputPath))
        {
            var json = await File.ReadAllTextAsync(outputPath);
            var outputs = JsonConvert.DeserializeObject<Dictionary<string, List<string>>>(json);
            foreach (var group in outputs ?? new())
            {
                foreach (var relPath in group.Value)
                {
                    vm.Artifacts.Add(new ArtifactViewModel
                    {
                        FileName = Path.GetFileName(relPath),
                        Token = $"{subJob.ParentJobId}:{subJob.Id}:{relPath.Replace(Path.DirectorySeparatorChar, ':')}",
                        Type = group.Key,
                        IsImage = FileExtensions.ImageFormats.Contains(Path.GetExtension(relPath).ToLower())
                    });
                }
            }
        }
        return vm;
    }*/

    public async Task<SubJobDetailsViewModel?> GetSubJobDetailsAsync(Guid subJobId)
    {
        var subJob = await _db.ProcessingJobs
            .Include(s => s.ParentJob)
            .FirstOrDefaultAsync(s => s.Id == subJobId);

        if (subJob == null) return null;

        var vm = new SubJobDetailsViewModel
        {
            SubJobId = subJobId,
            Status = subJob.Status,
            ResultMessage = subJob.ResultMessage
        };

        string jobRoot = subJob.ParentJob.PhysicalPathRoot;
        string subJobDir = Path.Combine(jobRoot, subJob.StepDirectoryName);
        string manifestDir = Path.Combine(jobRoot, MicsConstants.OrchestrationDirectory);
        string subJobManifestPath = Path.Combine(manifestDir, $"{subJob.Id}.json");

        // Load Script Metadata for Localization
        var scriptIndex = await _db.Scripts.FirstOrDefaultAsync(s => s.ScriptIdentifier == subJob.ScriptId);
        var scriptManifest = await LoadScriptManifestAsync(scriptIndex);
        var localization = await LoadLocalizationAsync(scriptIndex, scriptManifest);

        var inputLocMap = scriptManifest?.OrchestrationInputs?.ToDictionary(x => x.LabelParam, x => x.LocalizationLabel) ?? new();
        var outputLocMap = scriptManifest?.OrchestrationOutputs?.ToDictionary(x => x.LabelParam, x => x.LocalizationLabel) ?? new();

        string GetLoc(string key, Dictionary<string, string> map) =>
            map.TryGetValue(key, out var locKey) && localization.TryGetValue(locKey, out var val) ? val : key;

        // 1. Resolve Inputs (Scalars vs Assets)
        if (File.Exists(subJobManifestPath))
        {
            var orch = JsonConvert.DeserializeObject<JObject>(await File.ReadAllTextAsync(subJobManifestPath));
            var inputsBase = orch?["inputs_base"]?.ToObject<Dictionary<string, string>>() ?? new();

            string directInputPath = Path.Combine(subJobDir, MicsConstants.DirectInputFile);
            var directInputs = File.Exists(directInputPath)
                ? JsonConvert.DeserializeObject<Dictionary<string, object>>(await File.ReadAllTextAsync(directInputPath))
                : new();

            if (directInputs == null) directInputs = new();

            foreach (var inputEntry in inputsBase)
            {
                var label = inputEntry.Key;
                var relPath = inputEntry.Value;

                if (relPath.Contains(MicsConstants.DirectInputFile))
                {
                    if (directInputs.TryGetValue(label, out var val))
                    {
                        vm.ScalarInputs.Add(new ParameterValueViewModel
                        {
                            Label = label,
                            LocalizedLabel = GetLoc(label, inputLocMap),
                            Value = val?.ToString() ?? ""
                        });
                    }
                }
                else
                {
                    // Resolve physical files for the input collection (e.g., ./inputs/UUID)
                    string physicalInputPath = Path.Combine(jobRoot, relPath.Replace("./", ""));
                    if (Directory.Exists(physicalInputPath))
                    {
                        foreach (var file in Directory.GetFiles(physicalInputPath))
                        {
                            var fileName = Path.GetFileName(file);
                            var ext = Path.GetExtension(file).ToLower();
                            // Token part: inputs:uuid:file.ext
                            // We replace '/' with ':' because FileController splits on ':'
                            string tokenPath = relPath.Replace("./", "").Replace("/", ":") + ":" + fileName;

                            vm.InputArtifacts.Add(new ArtifactViewModel
                            {
                                FileName = fileName,
                                Token = $"{subJob.ParentJobId}:{subJob.Id}:{tokenPath}",
                                Type = label,
                                LocalizedType = GetLoc(label, inputLocMap),
                                IsImage = new[] { ".jpg", ".jpeg", ".png", ".webp" }.Contains(ext),
                                Extension = ext
                            });
                        }
                    }
                }
            }
        }

        // 2. Resolve Direct Outputs
        string directOutputPath = Path.Combine(subJobDir, MicsConstants.DirectOutputFile);
        if (File.Exists(directOutputPath))
        {
            var outputs = JsonConvert.DeserializeObject<Dictionary<string, object>>(await File.ReadAllTextAsync(directOutputPath));
            foreach (var kv in outputs ?? new())
            {
                vm.DirectOutputs.Add(new DirectOutputViewModel
                {
                    Label = kv.Key,
                    LocalizedLabel = GetLoc(kv.Key, outputLocMap),
                    Value = kv.Value?.ToString() ?? ""
                });
            }
        }

        // 3. Script Summary
        string summaryPath = Path.Combine(subJobDir, MicsConstants.ScriptSummaryFile);
        if (File.Exists(summaryPath))
        {
            var summaryObj = JsonConvert.DeserializeObject<object>(await File.ReadAllTextAsync(summaryPath));
            vm.SummaryJson = JsonConvert.SerializeObject(summaryObj, Formatting.Indented);
        }

        // 4. Resolve File Artifacts
        string fileOutputPath = Path.Combine(subJobDir, MicsConstants.FileOutputFile);
        if (File.Exists(fileOutputPath))
        {
            var fileOutputs = JsonConvert.DeserializeObject<Dictionary<string, List<string>>>(await File.ReadAllTextAsync(fileOutputPath));
            var explicitLabels = scriptManifest?.Outputs?.Select(o => o.Label).ToHashSet() ?? new HashSet<string>();

            foreach (var group in fileOutputs ?? new())
            {
                foreach (var relPathInGroup in group.Value)
                {
                    var ext = Path.GetExtension(relPathInGroup).ToLower();
                    // relPathInGroup is usually "masks/image.png"
                    // Physical path is directly relative to the scipt work dir
                    // Token needs step_X_uuid:path:to:file
                    string tokenPath = relPathInGroup.Replace("/", ":").Replace("\\", ":");

                    var artifact = new ArtifactViewModel
                    {
                        FileName = Path.GetFileName(relPathInGroup),
                        Token = $"{subJob.ParentJobId}:{subJob.Id}:{tokenPath}",
                        Type = group.Key,
                        LocalizedType = GetLoc(group.Key, outputLocMap),
                        IsImage = new[] { ".jpg", ".jpeg", ".png", ".webp" }.Contains(ext),
                        Extension = ext
                    };

                    if (explicitLabels.Contains(group.Key)) vm.ExplicitArtifacts.Add(artifact);
                    else vm.InternalArtifacts.Add(artifact);
                }
            }
        }

        return vm;
    }

    private async Task<ScriptManifest?> LoadScriptManifestAsync(ScriptIndex? scriptIndex)
    {
        if (scriptIndex == null) return null;
        string path = Path.Combine(_settings.CurrentValue.ScriptSourcePath, scriptIndex.ManifestDirectoryPath, MicsConstants.ScriptManifestFile);
        return File.Exists(path) ? JsonConvert.DeserializeObject<ScriptManifest>(await File.ReadAllTextAsync(path)) : null;
    }
    private async Task<Dictionary<string, string>> LoadLocalizationAsync(ScriptIndex? script, ScriptManifest? manifest)
    {
        if (script == null || manifest == null) return new();
        string localeFile = manifest.Localization?.Values.FirstOrDefault() ?? "";
        if (string.IsNullOrEmpty(localeFile)) return new();
        string path = Path.Combine(_settings.CurrentValue.ScriptSourcePath, script.ManifestDirectoryPath, MicsConstants.LocalizationDirectory, localeFile);
        return File.Exists(path) ? JsonConvert.DeserializeObject<Dictionary<string, string>>(await File.ReadAllTextAsync(path)) ?? new() : new();
    }


    public async Task<bool> PurgeJobAsync(Guid taskId)
    {
        var job = await _db.Jobs.Include(j => j.SubJobs).FirstOrDefaultAsync(j => j.Id == taskId);
        if (job == null) return false;

        try
        {
            // 1. Delete physical directory
            if (Directory.Exists(job.PhysicalPathRoot))
                Directory.Delete(job.PhysicalPathRoot, true);

            // 2. Delete Journal Failsafe
            DeleteJournal(taskId);

            // 3. Remove from database
            _db.Jobs.Remove(job);
            await _db.SaveChangesAsync();
            return true;
        }
        catch { return false; }
    }

    public async Task<bool> StopJobAsync(Guid taskId)
    {
        var task = await _db.Jobs.FindAsync(taskId);
        if (task == null) return false;

        task.Status = TaskStatusKeyword.Stopped;
        await _db.SaveChangesAsync();

        // Remove journal so failsafe recovery ignores stopped jobs
        DeleteJournal(taskId);
        _monitor.Stop(taskId);
        return true;
    }

    public async Task<bool> PauseJobAsync(Guid taskId)
    {
        var task = await _db.Jobs.FindAsync(taskId);
        if (task == null) return false;

        task.Status = TaskStatusKeyword.Paused;
        await _db.SaveChangesAsync();

        // Remove journal so failsafe recovery ignores paused jobs
        DeleteJournal(taskId);
        return true;
    }

    public async Task<bool> ResumeJobAsync(Guid taskId)
    {
        var task = await _db.Jobs.Include(j => j.SubJobs).FirstOrDefaultAsync(j => j.Id == taskId);
        if (task == null || task.Status == TaskStatusKeyword.Complete) return false;

        task.Status = TaskStatusKeyword.Pending;
        await _db.SaveChangesAsync();

        // Re-create failsafe journal for background worker
        await CreateJournalAsync(task);
        await _processingQueue.EnqueueAsync(taskId);
        return true;
    }

    public async Task<bool> RestartJobAsync(Guid taskId)
    {
        var task = await _db.Jobs.Include(j => j.SubJobs).FirstOrDefaultAsync(j => j.Id == taskId);
        if (task == null) return false;

        _isSystemHalted = false; // Reset circuit breaker

        foreach (var sub in task.SubJobs) { sub.Status = TaskStatusKeyword.Pending; }
        task.Status = TaskStatusKeyword.Pending;
        await _db.SaveChangesAsync();

        // Re-create failsafe journal
        await CreateJournalAsync(task);
        await _processingQueue.EnqueueAsync(taskId);
        return true;
    }

    public async Task CreateJournalAsync(OrchestratedTask task)
    {
        var journalDir = Path.Combine(_settings.CurrentValue.ResultsOutputPath, MicsConstants.JournalsDirectory);
        Directory.CreateDirectory(journalDir);

        var journal = new
        {
            TaskId = task.Id,
            Root = task.PhysicalPathRoot,
            SubJobs = task.SubJobs.OrderBy(s => s.Sequence).Select(s => s.Id).ToList(),
            Timestamp = DateTime.UtcNow
        };

        string journalPath = Path.Combine(journalDir, $"{task.Id}.{FileExtensions.Journal}");
        await File.WriteAllTextAsync(journalPath, JsonConvert.SerializeObject(journal, Formatting.Indented));
    }

    public void DeleteJournal(Guid taskId)
    {
        var path = Path.Combine(_settings.CurrentValue.ResultsOutputPath,
                                MicsConstants.JournalsDirectory,
                                $"{taskId}.{FileExtensions.Journal}");
        if (File.Exists(path)) File.Delete(path);
    }
}