
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using System.Text;

using Common.Infrastructure;
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
    public string ModuleId => ProcessorModuleEntryPoint.MODULE_ID;

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

            if (workerUser.Length > 13)
                logCallback($"[FAIL] User name '{workerUser}' is too long: Windows adds '-rights' and account names are limited to 20 characters (max 13 here).");

            // 2. Identity & Sync Checks (Worker)
            CheckAccountSync(workerUser, _settings.UserPassword, "Worker", logCallback);

            // 3. Identity & Sync Checks (Rights Manager)
            CheckAccountSync(rightsUser, _settings.RightsUserPassword, "Rights Manager", logCallback);

            // 4. Network: try a real connection as the worker
            await CheckWorkerNetworkAsync(workerUser, DpapiSecret.Unprotect(_settings.UserPassword), logCallback, ct);

            // 5. Node home: try to read a protected file (node.key / a database) as the worker
            await CheckWorkerNodeHomeReadAsync(workerUser, DpapiSecret.Unprotect(_settings.UserPassword), logCallback, ct);

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

    private void CheckAccountSync(string user, string? storedPass, string desc, Action<string> log)
    {
        bool exists = CheckUserExists(user);
        string? pass = DpapiSecret.Unprotect(storedPass);
        if (exists)
        {
            if (string.IsNullOrWhiteSpace(storedPass))
            {
                log($"[WARN] {desc} '{user}': MISSING PASSWORD in settings.");
            }
            else if (string.IsNullOrWhiteSpace(pass))
            {
                log($"[FAIL] {desc} '{user}': the stored password can't be decrypted by the account the app runs as (Provision again).");
            }
            else
            {
                bool authOk = TestLogon(user, pass);
                log(authOk ? $"[OK] {desc} '{user}': IN SYNC" : $"[FAIL] {desc} '{user}': PASSWORD MISMATCH (Sync required)");
                if (authOk && !DpapiSecret.IsProtected(storedPass))
                    log($"[WARN] {desc} '{user}': password stored in plain text; run Provision once to encrypt it.");
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

        if (string.IsNullOrWhiteSpace(workerUser) || workerUser.Length > 13)
        {
            logCallback($"[ERROR] User name '{workerUser}' must have 1-13 characters (Windows adds '-rights'; account names are limited to 20). Provisioning aborted.");
            return;
        }

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

        // Node home (databases, node.key, settings): deny the worker reading it. The explicit grants above outrank this
        // inherited deny, so script/results/executor folders inside the node home stay usable. Old deny removed first.
        string nodeHome = NodeHome.Folder.TrimEnd('\\', '/');
        string? skipReason = NodeHomeDenySkipReason(nodeHome, scriptPath, outputPath, executorDir);
        setupCommands.Add($"icacls \"{nodeHome}\" /remove:d \"{workerUser}\"");
        if (_settings.ProtectNodeHome && skipReason == null)
            setupCommands.Add($"icacls \"{nodeHome}\" /deny \"{workerUser}:(OI)(CI)R\"");
        else if (_settings.ProtectNodeHome)
            logCallback($"[WARN] Node home not protected: {skipReason}");

        // Network: block (or, with Allow Network, remove the block for) all traffic of the worker's user.
        // Windows doesn't filter loopback traffic, so local services stay reachable; Check Status tests it for real.
        string outRule = $"ProcessorSandbox-Out-{workerUser}", inRule = $"ProcessorSandbox-In-{workerUser}";
        setupCommands.Add($"Remove-NetFirewallRule -Name '{outRule}' -ErrorAction SilentlyContinue");
        setupCommands.Add($"Remove-NetFirewallRule -Name '{inRule}' -ErrorAction SilentlyContinue");
        if (!_settings.AllowNetwork)
        {
            string sid = new NTAccount(workerUser).Translate(typeof(SecurityIdentifier)).Value;
            string who = $"D:(A;;CC;;;{sid})";
            setupCommands.Add($"New-NetFirewallRule -Name '{outRule}' -DisplayName 'Processor sandbox: no network for {workerUser} (out)' -Direction Outbound -Action Block -Profile Any -LocalUser '{who}' | Out-Null");
            setupCommands.Add($"New-NetFirewallRule -Name '{inRule}' -DisplayName 'Processor sandbox: no network for {workerUser} (in)' -Direction Inbound -Action Block -Profile Any -LocalUser '{who}' | Out-Null");
        }

        string setupScript = BuildStepScript("Environment Lockdown", "Applying directory ownership, restricted access and network rules.", setupCommands.ToArray());

        if (await RunElevatedStepAsync(setupScript, ct))
            logCallback("[SUCCESS] Provisioning and Sync complete.");
        else
            logCallback("[ERROR] Environment setup failed during the elevated phase.");
    }

    private async Task SyncAccountAsync(string user, string? storedPass, string desc, Action<string> log, CancellationToken ct)
    {
        string field = user.EndsWith("-rights") ? nameof(_settings.RightsUserPassword) : nameof(_settings.UserPassword);
        string? currentPass = DpapiSecret.Unprotect(storedPass);
        bool exists = CheckUserExists(user);
        bool authenticated = exists && !string.IsNullOrEmpty(currentPass) && TestLogon(user, currentPass);

        if (exists && authenticated)
        {
            if (!DpapiSecret.IsProtected(storedPass))
            {
                // older settings kept it in plain text: store it encrypted (no UAC needed)
                await _settingsService.SetAsync(ModuleId, $"{nameof(OsSandboxSettings)}:{field}", DpapiSecret.Protect(currentPass!));
                log($"[INFO] {desc} user '{user}' is in sync; its password is now stored encrypted.");
            }
            else log($"[INFO] {desc} user '{user}' is already in sync.");
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
            await _settingsService.SetAsync(ModuleId, $"{nameof(OsSandboxSettings)}:{field}", DpapiSecret.Protect(newPass));
            log($"[SUCCESS] {desc} credentials updated and saved.");
        }
    }

    /// <summary>Starts PowerShell as the worker and tries an outside TCP connection; reports whether the block works.</summary>
    private async Task CheckWorkerNetworkAsync(string workerUser, string? workerPass, Action<string> log, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(workerPass) || !CheckUserExists(workerUser) || !TestLogon(workerUser, workerPass))
        {
            log("[SKIP] Network test needs a working worker account.");
            return;
        }
        try
        {
            using var secure = new SecureString();
            foreach (char c in workerPass) secure.AppendChar(c);
            secure.MakeReadOnly();
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                ArgumentList = { "-NoProfile", "-NonInteractive", "-Command",
                    "try { $c = New-Object Net.Sockets.TcpClient; $t = $c.ConnectAsync('1.1.1.1', 443); " +
                    "if ($t.Wait(5000) -and $c.Connected) { 'OPEN' } else { 'BLOCKED' } } catch { 'BLOCKED' }" },
                UserName = workerUser,
                Password = secure,
                LoadUserProfile = false,
                WorkingDirectory = Environment.SystemDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEndAsync();
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(TimeSpan.FromSeconds(20));
            try { await p.WaitForExitAsync(limit.Token); }
            catch (OperationCanceledException) { try { p.Kill(true); } catch { } log("[WARN] Network test as the worker timed out."); return; }

            bool open = (await output).Contains("OPEN");
            if (_settings.AllowNetwork)
                log(open ? "[OK] Network allowed for the worker (Allow Network is on)." : "[INFO] Allow Network is on, but the worker could not connect out.");
            else
                log(open ? "[FAIL] The worker CAN reach the internet: the firewall block isn't working (Provision again, or check that Windows Firewall is on)."
                         : "[OK] Network blocked for the worker (outside connection refused). Loopback is not filtered on Windows.");
        }
        catch (Exception ex)
        {
            log($"[WARN] Network test as the worker failed to run: {ex.Message}");
        }
    }

    /// <summary>Why the node-home deny must be skipped, or null. A deny on the very folder that also holds an explicit
    /// grant would win over that grant (explicit deny comes first), and a deny on a drive root is far too broad.</summary>
    private static string? NodeHomeDenySkipReason(string nodeHome, string scriptPath, string outputPath, string? executorDir)
    {
        if (string.IsNullOrWhiteSpace(nodeHome)) return "node home path is empty.";
        string full = Path.GetFullPath(nodeHome).TrimEnd('\\', '/');
        string? root = Path.GetPathRoot(full)?.TrimEnd('\\', '/');
        if (string.Equals(full, root, StringComparison.OrdinalIgnoreCase)) return $"the node home is a drive root ({full}).";
        foreach (var (path, what) in new[] { (scriptPath, "Script Source Path"), (outputPath, "Results Output Path"), (executorDir ?? "", "executor folder") })
        {
            if (!string.IsNullOrEmpty(path) && string.Equals(Path.GetFullPath(path).TrimEnd('\\', '/'), full, StringComparison.OrdinalIgnoreCase))
                return $"the {what} is the node home folder itself; point it to a subfolder.";
        }
        return null;
    }

    /// <summary>A file the worker must not read: node.key, else a database in the node home outside the sandbox folders.</summary>
    private string? FindProtectedSample()
    {
        string home = NodeHome.Folder;
        string key = Path.Combine(home, "Module_Data", "Main", "keys", "node.key");
        if (File.Exists(key)) return key;

        var allowed = new[] { _general.ScriptSourcePath, _general.ResultsOutputPath, _python.PythonExecutablePath }
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => Path.GetFullPath(p!).TrimEnd('\\', '/') + Path.DirectorySeparatorChar)
            .ToList();
        try
        {
            var opts = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 4, IgnoreInaccessible = true };
            return Directory.EnumerateFiles(home, "*.db", opts)
                .FirstOrDefault(f => !allowed.Any(a => f.StartsWith(a, StringComparison.OrdinalIgnoreCase)));
        }
        catch { return null; }
    }

    private async Task CheckWorkerNodeHomeReadAsync(string workerUser, string? workerPass, Action<string> log, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(workerPass) || !CheckUserExists(workerUser))
        {
            log("[INFO] Node home read test skipped: the worker account isn't set up.");
            return;
        }
        string? sample = FindProtectedSample();
        if (sample == null)
        {
            log("[INFO] Node home read test skipped: no node.key or database found in the node home.");
            return;
        }

        string quoted = sample.Replace("'", "''");
        string? result = await RunAsWorkerAsync(workerUser, workerPass,
            $"try {{ [IO.File]::OpenRead('{quoted}').Close(); 'READ' }} catch [UnauthorizedAccessException] {{ 'DENIED' }} catch {{ 'ERR ' + $_.Exception.Message }}",
            TimeSpan.FromSeconds(20), ct);

        string name = Path.GetFileName(sample);
        if (result == null) { log("[WARN] Node home read test as the worker did not finish."); return; }
        if (result.Contains("DENIED"))
            log(_settings.ProtectNodeHome ? $"[OK] The worker cannot read the node home ({name})."
                                          : $"[INFO] The worker cannot read the node home ({name}), although Protect Node Home is off.");
        else if (result.Contains("READ"))
            log(_settings.ProtectNodeHome ? $"[FAIL] The worker CAN read {sample}: run Setup User again (Protect Node Home)."
                                          : $"[WARN] The worker can read {sample} (Protect Node Home is off).");
        else
            log($"[WARN] Node home read test: {result.Trim()}");
    }

    /// <summary>Runs a PowerShell command as the worker and returns its output (null on timeout or start failure).</summary>
    private static async Task<string?> RunAsWorkerAsync(string workerUser, string workerPass, string command, TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            using var secure = new SecureString();
            foreach (char c in workerPass) secure.AppendChar(c);
            secure.MakeReadOnly();
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                ArgumentList = { "-NoProfile", "-NonInteractive", "-Command", command },
                UserName = workerUser,
                Password = secure,
                LoadUserProfile = false,
                WorkingDirectory = Environment.SystemDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEndAsync();
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(timeout);
            try { await p.WaitForExitAsync(limit.Token); }
            catch (OperationCanceledException) { try { p.Kill(true); } catch { } return null; }
            return await output;
        }
        catch (Exception ex)
        {
            return "ERR " + ex.Message;
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