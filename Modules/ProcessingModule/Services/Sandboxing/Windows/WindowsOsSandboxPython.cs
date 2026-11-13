
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.AccessControl;
using System.Security.Principal;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Win32.SafeHandles;

using ProcessingModule.Configuration;
using ProcessingModule.Database;
using ProcessingModule.Database.Models;
using ProcessingModule.Infrastructure;
using ProcessingModule.Infrastructure.Util;
using ProcessingModule.Services.Runtime;

using static Org.BouncyCastle.Math.EC.ECCurve;

namespace ProcessingModule.Services.Sandboxing.Windows;


/// <summary>
/// Windows OS-user sandbox: the executor runs as the worker account; a second "-rights" account owns the results folder
/// and grants / revokes the worker's access to one job folder.
/// Per step: job folder opened (shared by the steps of one job: reference-counted, closed by the last one), executor
/// started as the worker inside its own job object (nested in the app-wide kill-on-close job) that carries the optional
/// memory / CPU limits and is terminated after JobTimeoutMinutes. When the job folder closes, the rights account takes
/// ownership of everything the worker created and removes the worker's own permission entries, so a known path no
/// longer gets the worker back in.
/// </summary>
public class WindowsOsSandboxPython : ISandboxProcessing, IDisposable
{
    private readonly IOptionsMonitor<ProcessorSettings> _settings;
    private readonly IOptionsMonitor<PythonProcessingSettings> _pythonSettings;
    private readonly IOptionsMonitor<OsSandboxSettings> _osSandboxSettings;
    private readonly IDbContextFactory<ProcessorDbContext> _factory;
    private readonly ILogger<WindowsOsSandboxPython> _logger;

    public SandboxType GetSandboxType() => SandboxType.OSUser;

    private readonly SafeFileHandle _jobHandle;
    private bool _disposed;

    // job folder -> number of steps using it right now (steps of one job can run at once)
    private readonly Dictionary<string, int> _openJobRoots = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _jobRootLock = new(1, 1);

    public WindowsOsSandboxPython(
        IOptionsMonitor<ProcessorSettings> settings,
        IOptionsMonitor<PythonProcessingSettings> pythonSettings,
        IOptionsMonitor<OsSandboxSettings> osSandboxSettings,
        IDbContextFactory<ProcessorDbContext> dbContextFactory,
        ILogger<WindowsOsSandboxPython> logger
        )
    {
        _settings = settings;
        _pythonSettings = pythonSettings;
        _factory = dbContextFactory;
        _osSandboxSettings = osSandboxSettings;
        _logger = logger;

        _jobHandle = CreateJobObject(IntPtr.Zero, null);
        ConfigureJob(_jobHandle, memoryLimitMb: 0);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public async Task ExecuteJobAsync(Guid orchestrationId)
    {
        var osConfig = _osSandboxSettings.CurrentValue;
        var genConfig = _settings.CurrentValue;
        var pyConfig = _pythonSettings.CurrentValue;

        string workerUser = osConfig.UserName;
        string rightsUser = $"{workerUser}-rights";
        // stored DPAPI-protected by Provision (older settings: plain text, still accepted)
        string? workerPass = DpapiSecret.Unprotect(osConfig.UserPassword);
        string? rightsPass = DpapiSecret.Unprotect(osConfig.RightsUserPassword);

        // 1. PREEMPTIVE VALIDATION & AUTH SYNC CHECK
        if (string.IsNullOrWhiteSpace(workerPass) || string.IsNullOrWhiteSpace(rightsPass))
            throw new InvalidOperationException("OS Sandbox credentials are missing or can't be decrypted by the account the app runs as. Please run Provisioning.");

        if (!TestLogon(workerUser, workerPass))
            throw new InvalidOperationException($"Authentication failed for worker '{workerUser}'. OS password may have changed. Please re-provision.");

        if (!TestLogon(rightsUser, rightsPass))
            throw new InvalidOperationException($"Authentication failed for rights supervisor '{rightsUser}'. Please re-provision.");

        if (string.IsNullOrWhiteSpace(genConfig.ScriptSourcePath)) throw new InvalidOperationException("Script Source Path is not configured.");

        if (string.IsNullOrWhiteSpace(pyConfig.PythonExecutablePath)) throw new InvalidOperationException("Python/Executor Path is not configured.");

        // Same resolution as the no-sandbox runner: accept the executor folder or the .exe itself
        string executorPath = ExecutorPaths.Resolve(pyConfig.PythonExecutablePath);

        var workerSid = (SecurityIdentifier)new NTAccount(workerUser).Translate(typeof(SecurityIdentifier));
        var rightsSid = (SecurityIdentifier)new NTAccount(rightsUser).Translate(typeof(SecurityIdentifier));

        using var db = await _factory.CreateDbContextAsync();
        var subJob = await db.ProcessingJobs.Include(s => s.ParentJob).FirstOrDefaultAsync(s => s.Id == orchestrationId);
        if (subJob == null) throw new Exception($"Sub-job {orchestrationId} not found.");

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

        using var secureWorkerPass = new SecureString();
        foreach (char c in workerPass) secureWorkerPass.AppendChar(c);
        secureWorkerPass.MakeReadOnly();

        double timeoutMinutes = Math.Max(0, genConfig.JobTimeoutMinutes);

        // 2. Open the job folder to the worker (first step of this job grants, later ones only count)
        await OpenJobRootAsync(jobRoot, rightsUser, rightsPass, workerSid);
        try
        {
            // 3. This step's own job object: limits + timeout; kill-on-close also ends anything the step left running
            using var stepJob = CreateJobObject(IntPtr.Zero, null);
            ConfigureJob(stepJob, osConfig.MemoryLimitMb);
            SetCpuLimit(stepJob, osConfig.CpuLimitPercent);

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
                UserName = workerUser,
                Password = secureWorkerPass,
                UseShellExecute = false,
                LoadUserProfile = true,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };

            using var process = new Process { StartInfo = startInfo };
            process.Start();

            // App-wide job first (kill everything when the app exits), then the step job nested inside it
            if (!AssignProcessToJobObject(_jobHandle, process.Handle))
            {
                int err = Marshal.GetLastWin32Error();
                try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
                throw new System.ComponentModel.Win32Exception(err);
            }
            bool inStepJob = AssignProcessToJobObject(stepJob, process.Handle);
            if (!inStepJob)
                _logger.LogWarning("[Sandbox] Could not put the executor into its step job (error {Error}); memory/CPU limits are not applied to this step.",
                    Marshal.GetLastWin32Error());

            // Drain both pipes while waiting, otherwise a chatty script fills the buffer and hangs
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            bool timedOut = false;
            using (var timeout = timeoutMinutes > 0 ? new CancellationTokenSource(TimeSpan.FromMinutes(timeoutMinutes)) : new CancellationTokenSource())
            {
                try
                {
                    await process.WaitForExitAsync(timeout.Token);
                }
                catch (OperationCanceledException)
                {
                    timedOut = true;
                    if (!(inStepJob && TerminateJobObject(stepJob, 1460 /* ERROR_TIMEOUT */)))
                    {
                        try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
                    }
                    await process.WaitForExitAsync();
                }
            }
            await Task.WhenAll(stdoutTask, stderrTask);

            if (timedOut)
                throw new InvalidOperationException(
                    $"Script stopped: it ran longer than {timeoutMinutes:0.##} min (Job Timeout in Processing settings).");

            if (process.ExitCode != 0)
            {
                // Same contract as the no-sandbox runner: non-zero exit = script-level "Error"
                string errorOutput = stderrTask.Result;
                string limits = osConfig.MemoryLimitMb > 0 ? $" (memory limit {osConfig.MemoryLimitMb} MB)" : "";
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(errorOutput)
                    ? $"Executor exited with code {process.ExitCode}{limits}. {stdoutTask.Result}"
                    : errorOutput);
            }
        }
        finally
        {
            // 4. Close the job folder when the last step using it is done
            try { await CloseJobRootAsync(jobRoot, rightsUser, rightsPass, workerSid, rightsSid); }
            catch (Exception ex) { _logger.LogError(ex, "[Sandbox] Closing job folder {JobRoot} failed.", jobRoot); }
        }
    }

#pragma warning disable CA1416 // Validate platform compatibility
    private bool TestLogon(string user, string pass) => LogonUser(user, ".", pass, 2, 0, out SafeAccessTokenHandle _);

    private async Task OpenJobRootAsync(string jobRoot, string rightsUser, string rightsPass, SecurityIdentifier workerSid)
    {
        await _jobRootLock.WaitAsync();
        try
        {
            _openJobRoots.TryGetValue(jobRoot, out var n);
            if (n == 0)
            {
                // Inherited Modify: folders the app creates later (step folders, manifests) are covered too
                await RunAsUser(rightsUser, rightsPass, () =>
                    ApplyDirectorySecurity(jobRoot, workerSid, FileSystemRights.Modify, AccessControlType.Allow));
            }
            _openJobRoots[jobRoot] = n + 1;
        }
        finally { _jobRootLock.Release(); }
    }

    private async Task CloseJobRootAsync(string jobRoot, string rightsUser, string rightsPass, SecurityIdentifier workerSid, SecurityIdentifier rightsSid)
    {
        await _jobRootLock.WaitAsync();
        try
        {
            if (!_openJobRoots.TryGetValue(jobRoot, out var n)) return;
            if (n > 1) { _openJobRoots[jobRoot] = n - 1; return; }
            _openJobRoots.Remove(jobRoot);

            int failed = 0;
            await RunAsUser(rightsUser, rightsPass, () => failed = RevokeAndReclaim(jobRoot, workerSid, rightsSid));
            if (failed > 0)
                _logger.LogWarning("[Sandbox] {Count} item(s) in {JobRoot} could not be taken back from the worker (the script changed their permissions).",
                    failed, jobRoot);
        }
        finally { _jobRootLock.Release(); }
    }

    private async Task RunAsUser(string username, string password, Action action)
    {
        if (!LogonUser(username, ".", password, 2, 0, out SafeAccessTokenHandle safeToken))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());

        using (safeToken)
        {
            await WindowsIdentity.RunImpersonated(safeToken, async () => {
                action();
                await Task.CompletedTask;
            });
        }
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LogonUser(string lpszUsername, string lpszDomain, string lpszPassword, int dwLogonType, int dwLogonProvider, out SafeAccessTokenHandle phToken);

    private static void ApplyDirectorySecurity(string path, SecurityIdentifier identity, FileSystemRights rights, AccessControlType type)
    {
        if (!Directory.Exists(path)) return;
        var dInfo = new DirectoryInfo(path);
        var dSecurity = dInfo.GetAccessControl();

        var rule = new FileSystemAccessRule(identity, rights,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None, type);

        dSecurity.AddAccessRule(rule);
        dInfo.SetAccessControl(dSecurity);
    }

    /// <summary>
    /// Runs as the rights account when the last step of a job is done: removes the worker's entry on the job folder and,
    /// on everything inside, makes the rights account the owner of what the worker created, removes permission entries
    /// the worker added for itself and turns inheritance back on. Returns how many items could not be fixed.
    /// </summary>
    private static int RevokeAndReclaim(string jobRoot, SecurityIdentifier workerSid, SecurityIdentifier rightsSid)
    {
        if (!Directory.Exists(jobRoot)) return 0;
        int failed = 0;

        var root = new DirectoryInfo(jobRoot);
        var rootSec = root.GetAccessControl();
        if (RemoveExplicitRules(rootSec, workerSid)) root.SetAccessControl(rootSec);

        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 };
        foreach (var item in root.EnumerateFileSystemInfos("*", options))
        {
            try
            {
                FileSystemSecurity sec = item is DirectoryInfo d ? d.GetAccessControl() : ((FileInfo)item).GetAccessControl();
                bool changed = false;

                if (sec.GetOwner(typeof(SecurityIdentifier)) is SecurityIdentifier owner && owner.Equals(workerSid))
                {
                    sec.SetOwner(rightsSid);
                    changed = true;
                }
                if (RemoveExplicitRules(sec, workerSid)) changed = true;
                if (sec.AreAccessRulesProtected)
                {
                    sec.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);
                    changed = true;
                }

                if (!changed) continue;
                if (item is DirectoryInfo dir) dir.SetAccessControl((DirectorySecurity)sec);
                else ((FileInfo)item).SetAccessControl((FileSecurity)sec);
            }
            catch
            {
                failed++;
            }
        }
        return failed;
    }

    private static bool RemoveExplicitRules(FileSystemSecurity sec, SecurityIdentifier sid)
    {
        bool removed = false;
        foreach (FileSystemAccessRule rule in sec.GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier)))
        {
            if (rule.IdentityReference is SecurityIdentifier ruleSid && ruleSid.Equals(sid))
            {
                sec.RemoveAccessRuleSpecific(rule);
                removed = true;
            }
        }
        return removed;
    }

    #pragma warning restore CA1416 // Validate platform compatibility


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


    #region Win32 Job Objects Logic

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObject(IntPtr lpJobAttributes, string lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(SafeFileHandle hJob, int jobObjectInfoClass, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateJobObject(SafeFileHandle hJob, uint uExitCode);

    /// <summary>Kill-on-close, plus a memory limit for all processes of the job together (0 = none).</summary>
    private static void ConfigureJob(SafeFileHandle handle, int memoryLimitMb)
    {
        const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;
        const uint JOB_OBJECT_LIMIT_JOB_MEMORY = 0x200;
        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
            {
                LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE | (memoryLimitMb > 0 ? JOB_OBJECT_LIMIT_JOB_MEMORY : 0)
            },
            JobMemoryLimit = memoryLimitMb > 0 ? (UIntPtr)((ulong)memoryLimitMb * 1024UL * 1024UL) : UIntPtr.Zero
        };
        int length = Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
        IntPtr ptr = Marshal.AllocHGlobal(length);
        try
        {
            Marshal.StructureToPtr(info, ptr, false);
            if (!SetInformationJobObject(handle, 9 /* JobObjectExtendedLimitInformation */, ptr, (uint)length))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
        finally { Marshal.FreeHGlobal(ptr); }
    }

    /// <summary>Hard CPU cap for the job, in percent of the whole machine (0 = none).</summary>
    private void SetCpuLimit(SafeFileHandle handle, int percent)
    {
        if (percent <= 0 || percent >= 100) return;
        const uint JOB_OBJECT_CPU_RATE_CONTROL_ENABLE = 0x1, JOB_OBJECT_CPU_RATE_CONTROL_HARD_CAP = 0x4;
        var info = new JOBOBJECT_CPU_RATE_CONTROL_INFORMATION
        {
            ControlFlags = JOB_OBJECT_CPU_RATE_CONTROL_ENABLE | JOB_OBJECT_CPU_RATE_CONTROL_HARD_CAP,
            CpuRate = (uint)(percent * 100) // in 1/100 of a percent
        };
        int length = Marshal.SizeOf(typeof(JOBOBJECT_CPU_RATE_CONTROL_INFORMATION));
        IntPtr ptr = Marshal.AllocHGlobal(length);
        try
        {
            Marshal.StructureToPtr(info, ptr, false);
            if (!SetInformationJobObject(handle, 15 /* JobObjectCpuRateControlInformation */, ptr, (uint)length))
                _logger.LogWarning("[Sandbox] CPU limit not applied (error {Error}).", Marshal.GetLastWin32Error());
        }
        finally { Marshal.FreeHGlobal(ptr); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_CPU_RATE_CONTROL_INFORMATION { public uint ControlFlags; public uint CpuRate; }
    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION { public long PerProcessUserTimeLimit; public long PerJobUserTimeLimit; public uint LimitFlags; public UIntPtr MinimumWorkingSetSize; public UIntPtr MaximumWorkingSetSize; public uint ActiveProcessLimit; public long Affinity; public uint PriorityClass; public uint SchedulingClass; }
    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS { public ulong ReadOperationCount; public ulong WriteOperationCount; public ulong OtherOperationCount; public ulong ReadTransferCount; public ulong WriteTransferCount; public ulong OtherTransferCount; }
    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION { public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation; public IO_COUNTERS IoInfo; public UIntPtr ProcessMemoryLimit; public UIntPtr JobMemoryLimit; public UIntPtr PeakProcessMemoryLimit; public UIntPtr PeakJobMemoryLimit; }

    #endregion

    public void Dispose()
    {
        if (!_disposed)
        {
            _jobHandle?.Dispose();
            _disposed = true;
        }
    }
}