using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ProcessingModule.Configuration;
using ProcessingModule.Database;
using ProcessingModule.Infrastructure;
using ProcessingModule.Services.Runtime;
using ProcessingModule.Database.Models;

namespace ProcessingModule.Services.Sandboxing.Windows;

public class WindowsNoneSandboxPython : ISandboxProcessing
{
    private readonly IOptionsMonitor<ProcessorSettings> _settings;
    private readonly IOptionsMonitor<PythonProcessingSettings> _pythonSettings;
    private readonly IDbContextFactory<ProcessorDbContext> _factory;

    public SandboxType GetSandboxType() => SandboxType.None;

    public WindowsNoneSandboxPython(
        IOptionsMonitor<ProcessorSettings> settings,
        IOptionsMonitor<PythonProcessingSettings> pythonSettings,
        IDbContextFactory<ProcessorDbContext> dbContextFactory
        )
    {
        _settings = settings;
        _pythonSettings = pythonSettings;
        _factory = dbContextFactory;
    }

    /// <summary>
    /// Launches the standalone executor for a specific sub-job step.
    /// </summary>
    /// <param name="orchestrationId">The ID of the OrchestratedTaskSubJob (Step).</param>
    public async Task ExecuteJobAsync(Guid orchestrationId)
    {
        using var db = await _factory.CreateDbContextAsync();

        var subJob = await db.ProcessingJobs.Include(s => s.ParentJob).FirstOrDefaultAsync(s => s.Id == orchestrationId);
        if (subJob == null) throw new Exception($"Sub-job {orchestrationId} not found.");

        // 1. Resolve and Validate Executor Path
        string executorPath = _pythonSettings.CurrentValue.PythonExecutablePath;

        // Check if path is a directory; if so, look for executor.exe inside
        if (Directory.Exists(executorPath))
        {
            executorPath = Path.Combine(executorPath, "executor.exe");
        }

        if (!File.Exists(executorPath))
        {
            // Throw specific exception for infrastructure failure
            throw new FileNotFoundException($"Critical Error: Python Executor not found at {executorPath}");
        }

        string jobRoot = subJob.ParentJob.PhysicalPathRoot;

        var scriptIndex = await db.Scripts.FirstOrDefaultAsync(s => s.ScriptIdentifier == subJob.ScriptId);

        if (scriptIndex == null)
            throw new Exception($"Execution Error: Script metadata for {subJob.ScriptId} not found.");

        string scriptSourceDir = Path.Combine(_settings.CurrentValue.ScriptSourcePath, scriptIndex.ManifestDirectoryPath);
        string manifestRelPath = $"{MicsConstants.OrchestrationDirectory}/{subJob.Id}.json";

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = executorPath,
                Arguments = $"--processing-dir \"{jobRoot}\" --script-source-dir \"{scriptSourceDir}\" --manifest-file \"{manifestRelPath}\"",
                WorkingDirectory = jobRoot,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = startInfo };
            process.Start();
            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
            {
                string errorOutput = await process.StandardError.ReadToEndAsync();
                // Exit code errors are treated as script-level "Error"
                throw new InvalidOperationException(errorOutput);
            }
        }
        finally
        {

        }

    }

    /// <summary>
    /// Counts active instances of the executor.exe running on the system.
    /// </summary>
    public Task<int> GetActiveJobs()
    {
        // Get the filename without extension (e.g., "executor")
        string processName = Path.GetFileNameWithoutExtension(_pythonSettings.CurrentValue.PythonExecutablePath);
        var activeProcesses = Process.GetProcessesByName(processName);
        return Task.FromResult(activeProcesses.Length);
    }

    /// <summary>
    /// Returns the current database status of the job.
    /// </summary>
    public async Task<string> GetJobStatusAsync(Guid jobId)
    {
        using var db = await _factory.CreateDbContextAsync();
        var subJob = await db.ProcessingJobs.FindAsync(jobId);
        return subJob?.Status ?? "NotFound";
    }
}