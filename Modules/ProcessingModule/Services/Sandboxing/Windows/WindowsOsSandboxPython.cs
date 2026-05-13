
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.AccessControl;
using System.Security.Principal;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Win32.SafeHandles;

using ProcessingModule.Configuration;
using ProcessingModule.Database;
using ProcessingModule.Database.Models;
using ProcessingModule.Infrastructure;
using ProcessingModule.Services.Runtime;

using static Org.BouncyCastle.Math.EC.ECCurve;

namespace ProcessingModule.Services.Sandboxing.Windows;


public class WindowsOsSandboxPython : ISandboxProcessing, IDisposable
{
    private readonly IOptionsMonitor<ProcessorSettings> _settings;
    private readonly IOptionsMonitor<PythonProcessingSettings> _pythonSettings;
    private readonly IOptionsMonitor<OsSandboxSettings> _osSandboxSettings;
    private readonly IDbContextFactory<ProcessorDbContext> _factory;

    public SandboxType GetSandboxType() => SandboxType.OSUser;

    private readonly SafeFileHandle _jobHandle;
    private bool _disposed;

    public WindowsOsSandboxPython(
        IOptionsMonitor<ProcessorSettings> settings,
        IOptionsMonitor<PythonProcessingSettings> pythonSettings,
        IOptionsMonitor<OsSandboxSettings> osSandboxSettings,
        IDbContextFactory<ProcessorDbContext> dbContextFactory
        )
    {
        _settings = settings;
        _pythonSettings = pythonSettings;
        _factory = dbContextFactory;
        _osSandboxSettings = osSandboxSettings;

        _jobHandle = CreateJobObject(IntPtr.Zero, null);
        ConfigureJobToKillOnClose(_jobHandle);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public async Task ExecuteJobAsync(Guid orchestrationId)
    {
        var osConfig = _osSandboxSettings.CurrentValue;
        var genConfig = _settings.CurrentValue;
        var pyConfig = _pythonSettings.CurrentValue;

        string workerUser = osConfig.UserName;
        string rightsUser = $"{workerUser}-rights";
        string workerPass = osConfig.UserPassword;
        string rightsPass = osConfig.RightsUserPassword;

        // 1. PREEMPTIVE VALIDATION & AUTH SYNC CHECK
        if (string.IsNullOrWhiteSpace(workerPass) || string.IsNullOrWhiteSpace(rightsPass))
            throw new InvalidOperationException("OS Sandbox credentials missing. Please run Provisioning.");

        if (!TestLogon(workerUser, workerPass))
            throw new InvalidOperationException($"Authentication failed for worker '{workerUser}'. OS password may have changed. Please re-provision.");

        if (!TestLogon(rightsUser, rightsPass))
            throw new InvalidOperationException($"Authentication failed for rights supervisor '{rightsUser}'. Please re-provision.");

        if (string.IsNullOrWhiteSpace(genConfig.ScriptSourcePath)) throw new InvalidOperationException("Script Source Path is not configured.");
        
        string executorPath = pyConfig.PythonExecutablePath;
        if (string.IsNullOrWhiteSpace(executorPath)) throw new InvalidOperationException("Python/Executor Path is not configured.");


        using var db = await _factory.CreateDbContextAsync();
        var subJob = await db.ProcessingJobs.Include(s => s.ParentJob).FirstOrDefaultAsync(s => s.Id == orchestrationId);
        if (subJob == null) throw new Exception($"Sub-job {orchestrationId} not found.");

        string jobRoot = Path.GetFullPath(subJob.ParentJob.PhysicalPathRoot);
               executorPath = Path.Combine(executorPath, "executor.exe");

        var scriptIndex = await db.Scripts.FirstOrDefaultAsync(s => s.ScriptIdentifier == subJob.ScriptId);

        if (scriptIndex == null)
            throw new Exception($"Execution Error: Script metadata for {subJob.ScriptId} not found.");

        string scriptSourceDir = Path.Combine(_settings.CurrentValue.ScriptSourcePath, scriptIndex.ManifestDirectoryPath);
        string manifestRelPath = $"{MicsConstants.OrchestrationDirectory}/{subJob.Id}.json";

        using var secureWorkerPass = new SecureString();
        foreach (char c in workerPass) secureWorkerPass.AppendChar(c);
        secureWorkerPass.MakeReadOnly();

        try
        {
            // 2. Grant Access via Rights Supervisor
            await RunAsUser(rightsUser, rightsPass, () => {
                ApplyDirectorySecurity(jobRoot, workerUser, FileSystemRights.Modify, AccessControlType.Allow);
            });

            // 3. Start Execution
            var startInfo = new ProcessStartInfo
            {
                FileName = executorPath,
                Arguments = $"--processing-dir \"{jobRoot}\" --script-source-dir \"{scriptSourceDir}\" --manifest-file \"{manifestRelPath}\"",
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

            if (!AssignProcessToJobObject(_jobHandle, process.Handle))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());

            await process.WaitForExitAsync();
        }
        finally
        {
            // 4. Revoke Access via Rights Supervisor
            try { await RunAsUser(rightsUser, rightsPass, () => RemoveDirectorySecurity(jobRoot, workerUser)); } catch { }
        }
    }

#pragma warning disable CA1416 // Validate platform compatibility
    private bool TestLogon(string user, string pass) => LogonUser(user, ".", pass, 2, 0, out SafeAccessTokenHandle _);

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

    private void ApplyDirectorySecurity(string path, string identity, FileSystemRights rights, AccessControlType type)
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

    private void RemoveDirectorySecurity(string path, string identity)
    {
        var dInfo = new DirectoryInfo(path);
        var dSecurity = dInfo.GetAccessControl();
        var rules = dSecurity.GetAccessRules(true, true, typeof(NTAccount));

        foreach (FileSystemAccessRule rule in rules)
        {
            if (rule.IdentityReference.Value.Contains(identity, StringComparison.OrdinalIgnoreCase))
            {
                dSecurity.RemoveAccessRuleSpecific(rule);
            }
        }
        dInfo.SetAccessControl(dSecurity);
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

    private void ConfigureJobToKillOnClose(SafeFileHandle handle)
    {
        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION { LimitFlags = 0x2000 } // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
        };
        int length = Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
        IntPtr ptr = Marshal.AllocHGlobal(length);
        try
        {
            Marshal.StructureToPtr(info, ptr, false);
            if (!SetInformationJobObject(handle, 9, ptr, (uint)length))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
        finally { Marshal.FreeHGlobal(ptr); }
    }

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