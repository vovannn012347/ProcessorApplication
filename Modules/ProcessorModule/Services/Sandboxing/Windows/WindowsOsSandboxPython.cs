
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.AccessControl;
using System.Security.Principal;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Win32.SafeHandles;

using ProcessorModule.Configuration;
using ProcessorModule.Database;
using ProcessorModule.Infrastructure;
using ProcessorModule.Services.Runtime;

namespace ProcessorModule.Services.Sandboxing.Windows;

public class WindowsOsSandboxPython : ISandboxProcessing
{
    private readonly IOptionsMonitor<ProcessorSettings> _settings;
    private readonly IOptionsMonitor<PythonProcessingSettings> _pythonSettings;
    private readonly IOptionsMonitor<OsSandboxSettings> _osSandboxSettings;
    private readonly IDbContextFactory<ProcessorDbContext> _factory;

    public SandboxType GetSandboxType() => SandboxType.OSUser;

    // Windows Job Object handle to ensure child processes are killed on exit
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

        // Initialize Job Object for process management
        _jobHandle = CreateJobObject(IntPtr.Zero, null);
        ConfigureJobToKillOnClose(_jobHandle);
    }

#pragma warning disable CA1416 // Validate platform compatibility

    public async Task ExecuteJobAsync(Guid orchestrationId)
    {
        using var db = await _factory.CreateDbContextAsync();

        var subJob = await db.ProcessingJobs.Include(s => s.ParentJob).FirstOrDefaultAsync(s => s.Id == orchestrationId);
        if (subJob == null) throw new Exception($"Sub-job {orchestrationId} not found.");

        // 1. Resolve Executor and Paths
        string executorPath = _pythonSettings.CurrentValue.PythonExecutablePath;
        if (Directory.Exists(executorPath)) executorPath = Path.Combine(executorPath, "executor.exe");
        if (!File.Exists(executorPath)) throw new FileNotFoundException($"Executor not found: {executorPath}");

        string jobRoot = subJob.ParentJob.PhysicalPathRoot;
        var scriptIndex = await db.Scripts.FirstOrDefaultAsync(s => s.ScriptIdentifier == subJob.ScriptId);
        if (scriptIndex == null) throw new Exception($"Script metadata for {subJob.ScriptId} not found.");

        string scriptSourceDir = Path.Combine(_settings.CurrentValue.ScriptSourcePath, scriptIndex.ManifestDirectoryPath);
        string manifestRelPath = $"{MicsConstants.OrchestrationDirectory}/{subJob.Id}.json";

        // 2. Identity and Permissions Setup
        // We pull the restricted user from the OsSandboxSettings provided via IOptionsMonitor
        string restrictedUser = _osSandboxSettings.CurrentValue.UserName;

        try
        {
            // --- EPHEMERAL PERMISSION GRANT ---
            // Allow user to READ the script source code
            ApplyDirectorySecurity(scriptSourceDir, restrictedUser, FileSystemRights.ReadAndExecute, AccessControlType.Allow);

            // Allow user to MODIFY the job processing directory (including creating the orchestration folder)
            ApplyDirectorySecurity(jobRoot, restrictedUser, FileSystemRights.Modify, AccessControlType.Allow);

            var startInfo = new ProcessStartInfo
            {
                FileName = executorPath,
                Arguments = $"--processing-dir \"{jobRoot}\" --script-source-dir \"{scriptSourceDir}\" --manifest-file \"{manifestRelPath}\"",
                WorkingDirectory = jobRoot,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,

                // Impersonation
                UserName = restrictedUser,
                // Password management depends on your environment setup. 
                // If running as LocalSystem, you might not need the password for local users.
                // Password = GetSecurePassword(), 
                LoadUserProfile = false
            };

            using var process = new Process { StartInfo = startInfo };
            process.Start();

            // Assign to Job Object so the process dies if this service dies
            if (!AssignProcessToJobObject(_jobHandle, process.Handle))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());

            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
            {
                string errorOutput = await process.StandardError.ReadToEndAsync();
                throw new InvalidOperationException($"Execution Failed (Code {process.ExitCode}): {errorOutput}");
            }
        }
        finally
        {
            // --- PERMISSION REVOCATION ---
            // Clean up the security window once processing is done or if an error occurs
            RemoveDirectorySecurity(scriptSourceDir, restrictedUser);
            RemoveDirectorySecurity(jobRoot, restrictedUser);
        }
    }

    private void ApplyDirectorySecurity(string path, string identity, FileSystemRights rights, AccessControlType type)
    {
        var dInfo = new DirectoryInfo(path);
        var dSecurity = dInfo.GetAccessControl();

        // InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit 
        // ensures that folders created by Python (like orchestration dirs) get the same permissions.
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