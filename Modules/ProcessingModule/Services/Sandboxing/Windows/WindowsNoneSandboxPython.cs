using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ProcessingModule.Configuration;
using ProcessingModule.Database;
using ProcessingModule.Infrastructure;
using ProcessingModule.Infrastructure.Util;
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
        string executorPath = Path.GetFullPath(_pythonSettings.CurrentValue.PythonExecutablePath);

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

        // The executor runs with WorkingDirectory = jobRoot, so every path handed to it must be absolute
        // (relative ones would be resolved inside the job folder). The manifest path is the exception:
        // the executor contract defines it as relative to --processing-dir.
        string jobRoot = Path.GetFullPath(subJob.ParentJob.PhysicalPathRoot);

        // The exact build chosen when the job was created (older steps: best match by script_id/version)
        var scriptIndex = await ScriptBuildResolver.ResolveAsync(db, subJob);

        if (scriptIndex == null)
            throw new Exception($"Execution Error: Script build for {subJob.ScriptId} v{subJob.ScriptVersion} (build {subJob.ScriptIndexId}) not found.");

        if (!scriptIndex.IsAvailable)
            throw new Exception($"Execution Error: Script build {ScriptBuildResolver.Describe(scriptIndex)} is disabled or its files are missing.");

        string scriptSourceDir = Path.GetFullPath(Path.Combine(_settings.CurrentValue.ScriptSourcePath, scriptIndex.ManifestDirectoryPath));
        string manifestRelPath = $"{MicsConstants.OrchestrationDirectory}/{subJob.Id}.json";

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = executorPath,
                // ArgumentList quotes/escapes each value correctly. A hand-built string breaks on a
                // trailing backslash: "C:\dir\" ends with \" which Windows reads as an escaped quote,
                // so everything after it is glued into the previous argument.
                ArgumentList =
                {
                    "--processing-dir", Path.TrimEndingDirectorySeparator(jobRoot),
                    "--script-source-dir", Path.TrimEndingDirectorySeparator(scriptSourceDir),
                    "--manifest-file", manifestRelPath
                },
                WorkingDirectory = jobRoot,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = startInfo };
            process.Start();

            // Drain both pipes while waiting, otherwise a chatty script fills the buffer and hangs
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            await Task.WhenAll(stdoutTask, stderrTask);

            if (process.ExitCode != 0)
            {
                // Exit code errors are treated as script-level "Error"
                string errorOutput = stderrTask.Result;
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(errorOutput)
                    ? $"Executor exited with code {process.ExitCode}. {stdoutTask.Result}"
                    : errorOutput);
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