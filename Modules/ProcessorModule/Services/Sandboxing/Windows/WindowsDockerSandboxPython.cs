using System.Diagnostics;
using System.Security;
using System.Security.AccessControl;

using ProcessorModule.Infrastructure;
using ProcessorModule.Services.Runtime;

namespace ProcessorModule.Services.Sandboxing.Windows;

public class WindowsDockerSandboxPython : ISandboxProcessing
{
    public SandboxType GetSandboxType() => SandboxType.Docker;
    public WindowsDockerSandboxPython()
    {
    }

    // In a real production environment, you might use a pre-created 'SandboxUser' 
    // from a pool to avoid the overhead of constant user creation.
    private const string SandboxUserName = "ProcessSandboxUser";
    private const string SandboxPassword = "SecurePassword123!";


    public async Task<int> ExecuteAsync(
        string scriptPath,
        string entryPoint,
        string manifestPath,
        string workingDirectory,
        string readOnlyScriptsSource,
        CancellationToken token)
    {
        // 1. Assign Permissions (NTFS ACLs)
        // We ensure the SandboxUser can ONLY read the scripts but can WRITE to results
        ApplyPermissions(readOnlyScriptsSource, SandboxUserName, isReadOnly: true);
        ApplyPermissions(workingDirectory, SandboxUserName, isReadOnly: false);

        // 2. Prepare Process with User Credentials
        var securePassword = new SecureString();
        foreach (char c in SandboxPassword) securePassword.AppendChar(c);

        var startInfo = new ProcessStartInfo
        {
            FileName = "python",
            Arguments = $"\"{Path.Combine(scriptPath, entryPoint)}\" --manifest \"{manifestPath}\"",
            WorkingDirectory = workingDirectory,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,

            // Set the OS User context
            UserName = SandboxUserName,
            Password = securePassword,
            LoadUserProfile = false
        };

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        await process.WaitForExitAsync(token);
        return process.ExitCode;
    }

    private void ApplyPermissions(string path, string account, bool isReadOnly)
    {
        if (!Directory.Exists(path) && !File.Exists(path)) return;

        DirectoryInfo dInfo = new DirectoryInfo(path);
        DirectorySecurity dSecurity = dInfo.GetAccessControl();

        // Clear existing inherited permissions if necessary or just add the sandbox user
        var rights = isReadOnly
            ? FileSystemRights.ReadAndExecute
            : FileSystemRights.FullControl;

        var accessRule = new FileSystemAccessRule(
            account,
            rights,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow);

        dSecurity.AddAccessRule(accessRule);
        dInfo.SetAccessControl(dSecurity);
    }

    public Task ExecuteJobAsync(Guid orchestrationId)
    {
        throw new NotImplementedException();
    }

    public Task<int> GetActiveJobs()
    {
        throw new NotImplementedException();
    }

    public Task<string> GetJobStatusAsync(Guid jobId)
    {
        throw new NotImplementedException();
    }
}