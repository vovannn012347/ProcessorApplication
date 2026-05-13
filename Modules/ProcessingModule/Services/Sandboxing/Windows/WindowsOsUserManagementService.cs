
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

using Common.Interfaces;

using Microsoft.Extensions.Options;
using Microsoft.Win32.SafeHandles;

using ProcessingModule.Configuration;
using ProcessingModule.Infrastructure;

namespace ProcessingModule.Services.Sandboxing.Windows;

public class WindowsOsUserManagementService : IOsUserManagementService
{
    private readonly OsSandboxSettings _settings;
    private readonly ProcessorSettings _general;
    private readonly PythonProcessingSettings _python;
    private readonly ISettingService _settingsService;
    public string ModuleId => ProcessorModule.MODULE_ID;

    public WindowsOsUserManagementService(
        IOptionsMonitor<OsSandboxSettings> settings,
        IOptionsMonitor<ProcessorSettings> general,
        IOptionsMonitor<PythonProcessingSettings> python,
        ISettingService settingsService)
    {
        _settings = settings.CurrentValue;
        _general = general.CurrentValue;
        _python = python.CurrentValue;
        _settingsService = settingsService;
    }


    public async Task CheckStatusAsync(Action<string> logCallback, CancellationToken ct)
    {
        logCallback($"[CHECK] Verifying multi-user system identity and paths...");
        await Task.Delay(200, ct);

        try
        {
            // 1. Path Safety Validation
            bool pathsOk = ValidatePaths(logCallback);

            string workerUser = _settings.UserName;
            string rightsUser = $"{workerUser}-rights";

            // 2. Identity & Sync Checks (Worker)
            CheckAccountSync(workerUser, _settings.UserPassword, "Worker", logCallback);

            // 3. Identity & Sync Checks (Rights Manager)
            CheckAccountSync(rightsUser, _settings.RightsUserPassword, "Rights Manager", logCallback);

            // 4. Group Check
            if (!string.IsNullOrEmpty(_settings.GroupName))
            {
                bool groupExists = CheckGroupExists(_settings.GroupName);
                logCallback(groupExists ? $"[OK] Local group '{_settings.GroupName}' found." : $"[ABSENT] Local group '{_settings.GroupName}' missing.");
            }

            if (pathsOk) logCallback("[FINISH] Verification complete.");
            else logCallback("[FINISH] Verification complete with PATH ERRORS.");
        }
        catch (Exception ex)
        {
            logCallback($"[ERROR] {ex.Message}");
        }
    }

    private void CheckAccountSync(string user, string pass, string desc, Action<string> log)
    {
        bool exists = CheckUserExists(user);
        if (exists)
        {
            if (string.IsNullOrWhiteSpace(pass))
            {
                log($"[WARN] {desc} '{user}': MISSING PASSWORD in settings.");
            }
            else
            {
                bool authOk = TestLogon(user, pass);
                log(authOk ? $"[OK] {desc} '{user}': IN SYNC" : $"[FAIL] {desc} '{user}': PASSWORD MISMATCH (Sync required)");
            }
        }
        else
        {
            log($"[ABSENT] {desc} '{user}' does not exist.");
        }
    }

    private bool ValidatePaths(Action<string> log)
    {
        bool allOk = true;

        // Script Source
        if (string.IsNullOrWhiteSpace(_general.ScriptSourcePath)) { log("[FAIL] Script Source Path is EMPTY."); allOk = false; }
        else if (!Directory.Exists(_general.ScriptSourcePath)) { log($"[FAIL] Script Directory NOT FOUND: {_general.ScriptSourcePath}"); allOk = false; }
        else log("[OK] Script Source Path verified.");

        // Results Output
        if (string.IsNullOrWhiteSpace(_general.ResultsOutputPath)) { log("[FAIL] Results Output Path is EMPTY."); allOk = false; }
        else log("[OK] Results Output Path configured.");

        // Executor
        if (string.IsNullOrWhiteSpace(_python.PythonExecutablePath)) { log("[FAIL] Executor Path is EMPTY."); allOk = false; }
        else if (!File.Exists(_python.PythonExecutablePath) && !Directory.Exists(_python.PythonExecutablePath))
        {
            log($"[FAIL] Executor NOT FOUND: {_python.PythonExecutablePath}");
            allOk = false;
        }
        else log("[OK] Executor Path verified.");

        return allOk;
    }

    public async Task ProvisionUserAsync(Action<string> logCallback, CancellationToken ct)
    {
        logCallback("[INIT] Starting Multi-User Rights Sync & Provisioning...");

        // Safety Guard: Check paths before triggering UAC
        if (string.IsNullOrWhiteSpace(_general.ScriptSourcePath) || string.IsNullOrWhiteSpace(_general.ResultsOutputPath))
        {
            logCallback("[ERROR] Critical paths are not configured. Provisioning aborted.");
            return;
        }

        string workerUser = _settings.UserName;
        string rightsUser = $"{workerUser}-rights";

        // --- STEP 1: SYNC ACCOUNTS ---
        await SyncAccountAsync(workerUser, _settings.UserPassword, "Worker", logCallback, ct);
        await SyncAccountAsync(rightsUser, _settings.RightsUserPassword, "Rights Manager", logCallback, ct);

        // --- STEP 2: OWNERSHIP & ACLS ---
        logCallback("[ACTION] Verifying Ownership and Directory ACLs...");

        string scriptPath = Path.GetFullPath(_general.ScriptSourcePath).TrimEnd('\\', '/');
        string outputPath = Path.GetFullPath(_general.ResultsOutputPath).TrimEnd('\\', '/');
        string executorDir = string.Empty;
        if (!string.IsNullOrEmpty(_python.PythonExecutablePath))
            executorDir = Path.GetDirectoryName(Path.GetFullPath(_python.PythonExecutablePath))?.TrimEnd('\\', '/');

        if (!Directory.Exists(scriptPath)) Directory.CreateDirectory(scriptPath);
        if (!Directory.Exists(outputPath)) Directory.CreateDirectory(outputPath);

        var setupCommands = new List<string>
        {
            // 1. Hand over Ownership of processing root to the RIGHTS supervisor
            $"icacls \"{outputPath}\" /setowner \"{rightsUser}\" /T /C",
            
            // 2. Grant RIGHTS supervisor Full Control
            $"icacls \"{outputPath}\" /grant \"{rightsUser}:(OI)(CI)F\"",

            // 3. Grant WORKER read access to the processing root (to allow traversal)
            $"icacls \"{outputPath}\" /grant \"{workerUser}:R\"",

            // 4. Grant WORKER read access to Scripts
            $"icacls \"{scriptPath}\" /grant \"{workerUser}:(OI)(CI)R\""
        };

        if (!string.IsNullOrEmpty(executorDir) && Directory.Exists(executorDir))
        {
            setupCommands.Add($"icacls \"{executorDir}\" /grant \"{workerUser}:(OI)(CI)RX\"");
        }

        string setupScript = BuildStepScript("Environment Lockdown", "Applying directory ownership and restricted access rules.", setupCommands.ToArray());

        if (await RunElevatedStepAsync(setupScript, ct))
            logCallback("[SUCCESS] Provisioning and Sync complete.");
        else
            logCallback("[ERROR] Environment setup failed during the elevated phase.");
    }

    private async Task SyncAccountAsync(string user, string currentPass, string desc, Action<string> log, CancellationToken ct)
    {
        bool exists = CheckUserExists(user);
        bool authenticated = exists && !string.IsNullOrEmpty(currentPass) && TestLogon(user, currentPass);

        if (exists && authenticated)
        {
            log($"[INFO] {desc} user '{user}' is already in sync.");
            return;
        }

        string action = !exists ? "Creating" : "Resetting password for";
        log($"[ACTION] {action} {desc} user '{user}'...");

        string newPass = Guid.NewGuid().ToString("N").Substring(0, 12) + "A1!b";
        string cmd = !exists
            ? $"net user \"{user}\" \"{newPass}\" /add /active:yes /passwordchg:no /comment:\"Python Sandbox {desc}\""
            : $"net user \"{user}\" \"{newPass}\"";

        string script = BuildStepScript($"{desc} Setup", $"{action} the {desc} account.", cmd);

        if (await RunElevatedStepAsync(script, ct))
        {
            string field = user.EndsWith("-rights") ? nameof(_settings.RightsUserPassword) : nameof(_settings.UserPassword);
            await _settingsService.SetAsync(ModuleId, $"{nameof(OsSandboxSettings)}:{field}", newPass);
            log($"[SUCCESS] {desc} credentials updated and saved.");
        }
    }

    private bool TestLogon(string user, string pass)
    {
        return LogonUser(user, ".", pass, 2, 0, out SafeAccessTokenHandle _);
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LogonUser(string u, string d, string p, int lt, int lp, out SafeAccessTokenHandle t);

    private string BuildStepScript(string title, string explanation, params string[] commands)
    {
        var sb = new StringBuilder();
        sb.AppendLine("$ErrorActionPreference = 'Stop'");
        sb.AppendLine("try {");
        sb.AppendLine("  Clear-Host");
        sb.AppendLine($"  Write-Host '--- OS SETUP: {title.ToUpper()} ---' -ForegroundColor Cyan");
        sb.AppendLine($"  Write-Host 'PURPOSE: {explanation}' -ForegroundColor Gray");
        sb.AppendLine("  Write-Host ''");

        foreach (var cmd in commands)
        {
            // Escape single quotes for the PowerShell display string
            string displayCmd = cmd.Replace("'", "''");
            sb.AppendLine($"  Write-Host '> Executing: {displayCmd}' -ForegroundColor DarkGray");

            // Execute the command directly
            sb.AppendLine($"  & {cmd}");

            // Native command error checking
            sb.AppendLine("  if ($LASTEXITCODE -ne 0 -and $null -ne $LASTEXITCODE) { throw \"Command failed with exit code $LASTEXITCODE\" }");
        }

        sb.AppendLine("  Write-Host ''");
        sb.AppendLine("  Write-Host 'SUCCESS: Step completed. This window will close on input...' -ForegroundColor Green");
        sb.AppendLine("  Read-Host 'Press ENTER to close this window and proceed'");
        sb.AppendLine("  exit 0");
        sb.AppendLine("} catch {");
        sb.AppendLine("  Write-Host ''");
        sb.AppendLine("  Write-Host '!!! ERROR DETECTED !!!' -ForegroundColor Red");
        sb.AppendLine("  Write-Host $_.Exception.Message -ForegroundColor White");
        sb.AppendLine("  Read-Host 'Press ENTER to close this window and return to the application'");
        sb.AppendLine("  exit 1");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private async Task<bool> RunElevatedStepAsync(string psScript, CancellationToken ct)
    {
        // Use EncodedCommand to bypass all quoting issues
        // 1. Convert script to UTF-16LE (Unicode in .NET)
        byte[] scriptBytes = Encoding.Unicode.GetBytes(psScript);
        // 2. Convert to Base64
        string encodedScript = Convert.ToBase64String(scriptBytes);

        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            // Use -EncodedCommand instead of -Command
            Arguments = $"-ExecutionPolicy Bypass -EncodedCommand {encodedScript}",
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Normal
        };

        try
        {
            using var process = Process.Start(startInfo);
            if (process == null) return false;

            await process.WaitForExitAsync(ct);
            return process.ExitCode == 0;
        }
        catch (Win32Exception) { return false; } // UAC Denied
        catch { return false; }
    }
    private bool CheckUserExists(string username)
    {
        try
        {
            new NTAccount(username).Translate(typeof(SecurityIdentifier));
            return true;
        }
        catch { return false; }
    }

    private bool CheckGroupExists(string groupname)
    {
        try
        {
            new NTAccount(groupname).Translate(typeof(SecurityIdentifier));
            return true;
        }
        catch { return false; }
    }

    private async Task RunCommandAsync(string cmd, string args, Action<string> log)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = cmd,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        // We capture both output and error
        string output = await process.StandardOutput.ReadToEndAsync();
        string error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
        {
            // 'net' commands return 0 on success. Some 'errors' are acceptable (e.g. user already in group)
            if (output.Contains("already a member") || output.Contains("already exists"))
            {
                log($"[INFO] {output.Trim()}");
            }
            else
            {
                log($"[ERROR] Code {process.ExitCode}: {error.Trim()} {output.Trim()}");
            }
        }
        else
        {
            log($"[SUCCESS] Windows accepted the command.");
        }
    }
}