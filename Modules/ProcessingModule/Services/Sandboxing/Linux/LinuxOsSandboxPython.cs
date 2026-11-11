using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using ProcessingModule.Configuration;
using ProcessingModule.Database;
using ProcessingModule.Infrastructure;
using ProcessingModule.Infrastructure.Util;
using ProcessingModule.Services.Runtime;

namespace ProcessingModule.Services.Sandboxing.Linux;

/// <summary>
/// OS-user sandbox on Linux. Prepared once by processor-sandbox-setup.sh (Settings → Provision):
/// worker user + sandbox group, app user in that group, sudo rule "app may run commands as the worker", scripts/executor
/// readable by the group, results folder traverse-only for the group, worker network blocked (loopback included).
///
/// Per step:
/// 1. The job folder is opened to the group (chgrp -R + g+rwX, setgid dirs so new files keep the group). The app owns
///    the job folder, so no root is needed. Several steps of one job share it: it stays open until the last one ends.
/// 2. The executor runs as the worker: sudo -n -u worker, clean environment, HOME/TMPDIR in a per-step temp folder inside
///    the job, umask 007 (files the worker creates stay readable/deletable by the app through the group), and
///    `timeout` enforces JobTimeoutMinutes (0 = no limit).
/// 3. The job folder is closed again (group back to the app's, mode 0700). Linux checks every folder on the way, so the
///    worker can't reach anything in it afterwards, even files it owns. It can't list the results folder either.
/// No passwords are stored or used. Running executors are killed when the app stops (and leftovers on the first step).
/// </summary>
public sealed class LinuxOsSandboxPython : ISandboxProcessing, IDisposable
{
    private readonly IOptionsMonitor<ProcessorSettings> _settings;
    private readonly IOptionsMonitor<PythonProcessingSettings> _pythonSettings;
    private readonly IOptionsMonitor<OsSandboxSettings> _osSandboxSettings;
    private readonly IDbContextFactory<ProcessorDbContext> _factory;
    private readonly ILogger<LinuxOsSandboxPython> _logger;

    // job folder -> number of steps currently running in it
    private readonly Dictionary<string, int> _openJobRoots = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _jobRootLock = new(1, 1);
    private readonly ConcurrentDictionary<int, Process> _running = new();
    private readonly SemaphoreSlim _orphanLock = new(1, 1);
    private bool _orphansCleaned;
    private string? _lastWorker;
    private bool _disposed;

    public SandboxType GetSandboxType() => SandboxType.OSUser;

    public LinuxOsSandboxPython(
        IOptionsMonitor<ProcessorSettings> settings,
        IOptionsMonitor<PythonProcessingSettings> pythonSettings,
        IOptionsMonitor<OsSandboxSettings> osSandboxSettings,
        IDbContextFactory<ProcessorDbContext> dbContextFactory,
        ILogger<LinuxOsSandboxPython> logger)
    {
        _settings = settings;
        _pythonSettings = pythonSettings;
        _osSandboxSettings = osSandboxSettings;
        _factory = dbContextFactory;
        _logger = logger;
    }

    public async Task ExecuteJobAsync(Guid orchestrationId)
    {
        var genConfig = _settings.CurrentValue;
        var names = LinuxSandboxNames.From(_osSandboxSettings.CurrentValue);
        _lastWorker = names.Worker;

        if (string.IsNullOrWhiteSpace(genConfig.ScriptSourcePath)) throw new InvalidOperationException("Script Source Path is not configured.");
        string executorPath = ExecutorPaths.Resolve(_pythonSettings.CurrentValue.PythonExecutablePath);

        using var db = await _factory.CreateDbContextAsync();
        var subJob = await db.ProcessingJobs.Include(s => s.ParentJob).FirstOrDefaultAsync(s => s.Id == orchestrationId);
        if (subJob == null) throw new Exception($"Sub-job {orchestrationId} not found.");

        // Same contract as the other runners: absolute paths, manifest relative to --processing-dir
        string jobRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(subJob.ParentJob.PhysicalPathRoot));

        var scriptIndex = await ScriptBuildResolver.ResolveAsync(db, subJob);
        if (scriptIndex == null)
            throw new Exception($"Execution Error: Script build for {subJob.ScriptId} v{subJob.ScriptVersion} (build {subJob.ScriptIndexId}) not found.");
        if (!scriptIndex.IsAvailable)
            throw new Exception($"Execution Error: Script build {ScriptBuildResolver.Describe(scriptIndex)} is disabled or its files are missing.");

        string scriptSourceDir = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(Path.Combine(genConfig.ScriptSourcePath, scriptIndex.ManifestDirectoryPath)));
        string manifestRelPath = $"{MicsConstants.OrchestrationDirectory}/{subJob.Id}.json";

        // 1. Is the one-time setup in place? (sudo rule, and this process in the sandbox group)
        await EnsureReadyAsync(names);
        await KillOrphansOnceAsync(names.Worker);

        // Per-step HOME/TMPDIR inside the job folder: the worker has no home, and /tmp would be shared between jobs
        string stepTemp = Path.Combine(jobRoot, ".sandbox", subJob.Id.ToString("N"));
        Directory.CreateDirectory(stepTemp);

        await OpenJobRootAsync(jobRoot, names);
        try
        {
            var args = new List<string>
            {
                "-n", "-u", names.Worker, "--",
                "/usr/bin/env", "-i",
                "PATH=/usr/local/bin:/usr/bin:/bin",
                "LANG=C.UTF-8",
                $"HOME={stepTemp}",
                $"TMPDIR={stepTemp}",
                $"XDG_CACHE_HOME={stepTemp}",
                $"MPLCONFIGDIR={stepTemp}",
                // umask 007: what the worker creates stays readable/deletable by the app through the group
                "/bin/sh", "-c", "umask 007; exec \"$@\"", "sh"
            };

            int timeoutSeconds = (int)Math.Round(Math.Max(0, genConfig.JobTimeoutMinutes) * 60);
            if (timeoutSeconds > 0)
            {
                args.AddRange(new[] { "timeout", "--kill-after=30s", timeoutSeconds.ToString(CultureInfo.InvariantCulture) + "s" });
            }

            args.AddRange(new[]
            {
                executorPath,
                "--processing-dir", jobRoot,
                "--script-source-dir", scriptSourceDir,
                "--manifest-file", manifestRelPath
            });

            var startInfo = new ProcessStartInfo
            {
                FileName = "sudo",
                WorkingDirectory = jobRoot,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true
            };
            foreach (var a in args) startInfo.ArgumentList.Add(a);

            using var process = new Process { StartInfo = startInfo };
            process.Start();
            process.StandardInput.Close();
            _running[process.Id] = process;

            try
            {
                // Drain both pipes while waiting, otherwise a chatty script fills the buffer and hangs
                var stdoutTask = process.StandardOutput.ReadToEndAsync();
                var stderrTask = process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();
                await Task.WhenAll(stdoutTask, stderrTask);

                int exit = process.ExitCode;
                string stderr = stderrTask.Result;

                if (exit == 0) return;

                if (timeoutSeconds > 0 && (exit == 124 || exit == 137))
                    throw new InvalidOperationException(
                        $"Script stopped: it ran longer than {genConfig.JobTimeoutMinutes:0.##} min (Job Timeout in Processing settings).");

                if (stderr.Contains("sudo:", StringComparison.Ordinal) && stderr.Contains("password", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        $"The app may not run commands as '{names.Worker}' (sudo rule missing). Run Provision in Processing settings.");

                // Same contract as the other runners: non-zero exit = script-level "Error"
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(stderr)
                    ? $"Executor exited with code {exit}. {stdoutTask.Result}"
                    : stderr);
            }
            finally
            {
                _running.TryRemove(process.Id, out _);
            }
        }
        finally
        {
            try { Directory.Delete(stepTemp, recursive: true); }
            catch (Exception ex) { _logger.LogDebug(ex, "Could not remove sandbox temp folder {Folder}", stepTemp); }

            try { await CloseJobRootAsync(jobRoot); }
            catch (Exception ex) { _logger.LogWarning(ex, "Could not close job folder {Folder} to the sandbox worker", jobRoot); }
        }
    }

    private static async Task EnsureReadyAsync(LinuxSandboxNames names)
    {
        var sudo = await LinuxShell.RunAsync("sudo", "-n", "-u", names.Worker, "true");
        if (!sudo.Ok)
            throw new InvalidOperationException(
                $"OS sandbox is not set up: the app can't run commands as '{names.Worker}' ({sudo.Text}). Run Provision in Processing settings.");

        if (!await LinuxShell.IsRootAsync() && !await LinuxShell.ProcessInGroupAsync(names.Group))
            throw new InvalidOperationException(
                $"The app process is not in group '{names.Group}' yet. Restart the app once after provisioning.");
    }

    /// <summary>Executors left over from a crash or a hard stop: killed before the first step of this app run.</summary>
    private async Task KillOrphansOnceAsync(string worker)
    {
        if (_orphansCleaned) return;
        await _orphanLock.WaitAsync();
        try
        {
            if (_orphansCleaned) return;
            if (_running.IsEmpty)
                await LinuxShell.RunAsync("sudo", "-n", "-u", worker, "pkill", "-KILL", "-u", worker);
            _orphansCleaned = true;
        }
        finally { _orphanLock.Release(); }
    }

    private async Task OpenJobRootAsync(string jobRoot, LinuxSandboxNames names)
    {
        await _jobRootLock.WaitAsync();
        try
        {
            string appUser = await LinuxShell.AppUserAsync();

            // Every step re-applies this (also when the folder is already open): folders the app created since the last
            // step (the new step's folder, manifests) must become group-writable too. Errors on files the worker owns
            // (the app can't change those) are expected and ignored; those already have the right group and mode.
            await LinuxShell.RunAsync("chgrp", "-R", "-f", names.Group, jobRoot);
            await LinuxShell.RunAsync("chmod", "-R", "-f", "g+rwX,o-rwx", jobRoot);
            await LinuxShell.RunAsync("find", jobRoot, "-type", "d", "-user", appUser, "-exec", "chmod", "g+s", "{}", "+");

            var group = await LinuxShell.RunAsync("stat", "-c", "%G", jobRoot);
            if (group.StdOut.Trim() != names.Group)
                throw new InvalidOperationException(
                    $"Could not open the job folder to group '{names.Group}' (it is '{group.StdOut.Trim()}'). " +
                    "If provisioning just ran, restart the app once.");

            _openJobRoots[jobRoot] = _openJobRoots.TryGetValue(jobRoot, out var n) ? n + 1 : 1;
        }
        finally { _jobRootLock.Release(); }
    }

    private async Task CloseJobRootAsync(string jobRoot)
    {
        await _jobRootLock.WaitAsync();
        try
        {
            if (!_openJobRoots.TryGetValue(jobRoot, out var n)) return;
            if (n > 1) { _openJobRoots[jobRoot] = n - 1; return; }
            _openJobRoots.Remove(jobRoot);

            // Only the job folder itself: once the worker can't enter it, nothing below is reachable
            await LinuxShell.RunAsync("chgrp", await LinuxShell.AppGroupAsync(), jobRoot);
            await LinuxShell.RunAsync("chmod", "0700", jobRoot);
        }
        finally { _jobRootLock.Release(); }
    }

    /// <summary>Executors this sandbox is running right now.</summary>
    public Task<int> GetActiveJobs() => Task.FromResult(_running.Count);

    public async Task<string> GetJobStatusAsync(Guid jobId)
    {
        using var db = await _factory.CreateDbContextAsync();
        var subJob = await db.ProcessingJobs.FindAsync(jobId);
        return subJob?.Status ?? "NotFound";
    }

    /// <summary>App shutdown: kill every process of the worker (all running executors and whatever they started).</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_running.IsEmpty || _lastWorker == null) return;
        try
        {
            LinuxShell.RunAsync("sudo", "-n", "-u", _lastWorker, "pkill", "-KILL", "-u", _lastWorker)
                .Wait(TimeSpan.FromSeconds(5));
        }
        catch { /* shutting down anyway */ }
    }
}
