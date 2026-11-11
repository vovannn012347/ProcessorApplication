using System.Net;
using System.Net.Sockets;

using Microsoft.Extensions.Options;

using ProcessingModule.Configuration;
using ProcessingModule.Infrastructure;

namespace ProcessingModule.Services.Sandboxing.Linux;

/// <summary>
/// Settings → OS sandbox → Provision / Check on Linux.
///
/// Provisioning needs root exactly once (create the worker user and group, the sudo rule, folder groups, the network
/// block). It runs Sandboxing/linux/processor-sandbox-setup.sh from the module folder:
/// - directly, if the app runs as root;
/// - through "sudo -n" if this account may use sudo without a password;
/// - otherwise it prints the exact command for the admin to run in a terminal (sudo asks for the admin's password there).
/// No root password is ever typed into, sent through or stored by the app, and no worker password exists on Linux.
/// </summary>
public class LinuxOsUserManagementService : IOsUserManagementService
{
    private readonly OsSandboxSettings _settings;
    private readonly ProcessorSettings _general;
    private readonly PythonProcessingSettings _python;

    public LinuxOsUserManagementService(
        IOptionsMonitor<OsSandboxSettings> settings,
        IOptionsMonitor<ProcessorSettings> general,
        IOptionsMonitor<PythonProcessingSettings> python)
    {
        _settings = settings.CurrentValue;
        _general = general.CurrentValue;
        _python = python.CurrentValue;
    }

    private static string SetupScriptPath =>
        Path.Combine(Path.GetDirectoryName(typeof(LinuxOsUserManagementService).Assembly.Location)!,
            "Sandboxing", "linux", "processor-sandbox-setup.sh");

    public async Task CheckStatusAsync(Action<string> log, CancellationToken ct)
    {
        log("[CHECK] Verifying the Linux OS sandbox...");

        LinuxSandboxNames names;
        try { names = LinuxSandboxNames.From(_settings); }
        catch (InvalidOperationException ex) { log($"[FAIL] {ex.Message}"); log("[FINISH] Verification complete with ERRORS."); return; }

        bool allOk = ValidatePaths(log);
        string w = names.Worker, g = names.Group;
        log($"[INFO] Worker '{w}', group '{g}' (Linux names are lower case). No passwords are used on Linux.");

        // Accounts
        var id = await LinuxShell.RunAsync("id", "-u", w);
        if (!id.Ok) { log($"[ABSENT] Worker user '{w}' does not exist. Run Provision."); allOk = false; }
        else
        {
            var primary = (await LinuxShell.RunAsync("id", "-gn", w)).StdOut.Trim();
            log(primary == g ? $"[OK] Worker '{w}' exists (primary group '{g}')." : $"[WARN] Worker '{w}' has primary group '{primary}', expected '{g}'. Run Provision.");
        }

        if (!(await LinuxShell.RunAsync("getent", "group", g)).Ok) { log($"[ABSENT] Group '{g}' does not exist. Run Provision."); allOk = false; }
        else if (await LinuxShell.IsRootAsync()) log("[OK] The app runs as root (group membership not needed).");
        else if (await LinuxShell.ProcessInGroupAsync(g)) log($"[OK] The app process is in group '{g}'.");
        else { log($"[WARN] The app process is not in group '{g}'. If provisioning just ran, restart the app."); allOk = false; }

        // sudo rule
        var sudo = await LinuxShell.RunAsync("sudo", "-n", "-u", w, "true");
        if (sudo.Ok) log($"[OK] The app can run commands as '{w}' (sudo rule).");
        else { log($"[FAIL] The app can't run commands as '{w}': {FirstLine(sudo.Text)}. Run Provision."); log("[FINISH] Verification complete with ERRORS."); return; }

        // What the worker can and can't do
        string scripts = Full(_general.ScriptSourcePath);
        string results = Full(_general.ResultsOutputPath);
        string? executor = TryExecutor();

        allOk &= await ExpectAsync(log, w, true, $"[OK] Worker can read the scripts folder.", $"[FAIL] Worker can't read the scripts folder {scripts}.", "-r", scripts, "-a", "-x", scripts);
        allOk &= await ExpectAsync(log, w, false, $"[OK] Worker can't write to the scripts folder.", $"[FAIL] Worker CAN write to the scripts folder {scripts}.", "-w", scripts);
        if (executor != null)
            allOk &= await ExpectAsync(log, w, true, $"[OK] Worker can run the executor.", $"[FAIL] Worker can't run the executor {executor}.", "-x", executor);
        allOk &= await ExpectAsync(log, w, true, $"[OK] Worker can pass through the results folder.", $"[FAIL] Worker can't reach the results folder {results}.", "-x", results);
        allOk &= await ExpectAsync(log, w, false, $"[OK] Worker can't list the results folder (job ids stay hidden).", $"[WARN] Worker CAN list the results folder {results}.", "-r", results);

        // Network: loopback (could reach this app's own endpoints) and outside
        allOk &= await CheckNetworkAsync(log, w, ct);

        log(allOk ? "[FINISH] Verification complete." : "[FINISH] Verification complete with WARNINGS/ERRORS.");
    }

    public async Task ProvisionUserAsync(Action<string> log, CancellationToken ct)
    {
        log("[INIT] Linux OS sandbox provisioning...");

        LinuxSandboxNames names;
        try { names = LinuxSandboxNames.From(_settings); }
        catch (InvalidOperationException ex) { log($"[ERROR] {ex.Message}"); return; }

        if (string.IsNullOrWhiteSpace(_general.ScriptSourcePath) || string.IsNullOrWhiteSpace(_general.ResultsOutputPath))
        {
            log("[ERROR] Critical paths are not configured. Provisioning aborted.");
            return;
        }

        string scripts = Full(_general.ScriptSourcePath);
        string results = Full(_general.ResultsOutputPath);
        string? executorDir = ExecutorPaths.GetDirectory(_python.PythonExecutablePath);
        if (executorDir == null) { log("[ERROR] Executor Path is not configured. Provisioning aborted."); return; }

        Directory.CreateDirectory(scripts);
        Directory.CreateDirectory(results);

        string script = SetupScriptPath;
        if (!File.Exists(script)) { log($"[ERROR] Setup script missing: {script}"); return; }

        string appUser = await LinuxShell.AppUserAsync();
        var args = new List<string>
        {
            script,
            "--app-user", appUser,
            "--worker", names.Worker,
            "--group", names.Group,
            "--scripts", scripts,
            "--results", results,
            "--executor-dir", executorDir
        };

        // The node home (databases, keys, settings; the program folder itself in the single-node layout): no access for
        // "others", so the worker can't open those files through a folder it may pass through.
        args.Add("--protect");
        args.Add(Path.TrimEndingDirectorySeparator(Common.Infrastructure.NodeHome.Folder));

        string file;
        List<string> runArgs;
        if (await LinuxShell.IsRootAsync())
        {
            file = "bash"; runArgs = args;
            log("[INFO] The app runs as root: running the setup directly.");
        }
        else if ((await LinuxShell.RunAsync("sudo", "-n", "true")).Ok)
        {
            file = "sudo"; runArgs = new List<string> { "-n", "bash" }; runArgs.AddRange(args);
            log("[INFO] Passwordless sudo available: running the setup through sudo.");
        }
        else
        {
            // No way to become root from here without a password - and the app doesn't take one.
            log("[ACTION] This step needs root once. Run this in a terminal on this machine (sudo asks for YOUR password there):");
            log("[CMD] " + LinuxShell.CommandLine("sudo bash", args));
            log("[ACTION] Then restart the app and press Check.");
            log("[INFO] After that the app needs no root and no passwords: it runs scripts as the worker through the sudo rule the setup installs.");
            return;
        }

        int exit = await LinuxShell.StreamAsync(file, runArgs, line => { log(line); return Task.CompletedTask; }, ct);
        if (exit != 0)
        {
            log($"[ERROR] Setup failed (exit code {exit}).");
            return;
        }

        if (await LinuxShell.IsRootAsync() || await LinuxShell.ProcessInGroupAsync(names.Group))
            log("[SUCCESS] Provisioning complete.");
        else
            log($"[SUCCESS] Provisioning complete. RESTART THE APP once: the new membership in group '{names.Group}' only applies to newly started processes.");
    }

    // ------------------------------------------------------------------ helpers

    private static string Full(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static string FirstLine(string text)
    {
        var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
        return string.IsNullOrEmpty(line) ? "no details" : line;
    }

    private string? TryExecutor()
    {
        try { return ExecutorPaths.Resolve(_python.PythonExecutablePath); }
        catch { return null; }
    }

    /// <summary>Runs <c>test &lt;args&gt;</c> as the worker and compares with what is expected.</summary>
    private static async Task<bool> ExpectAsync(Action<string> log, string worker, bool expected, string okText, string badText, params string[] testArgs)
    {
        var args = new List<string> { "-n", "-u", worker, "test" };
        args.AddRange(testArgs);
        bool result = (await LinuxShell.RunAsync("sudo", args)).Ok;
        log(result == expected ? okText : badText);
        return result == expected;
    }

    private static async Task<bool> CheckNetworkAsync(Action<string> log, string worker, CancellationToken ct)
    {
        bool ok = true;

        // Loopback: listen on a free local port and see whether the worker can connect to it
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            _ = listener.AcceptTcpClientAsync();
            var r = await LinuxShell.RunAsync("sudo", new[] { "-n", "-u", worker, "timeout", "3", "bash", "-c", $"exec 3<>/dev/tcp/127.0.0.1/{port}" }, ct);
            if (r.Ok) { log("[WARN] Worker CAN connect to this machine (loopback): scripts could call the app's own endpoints. Run Provision (network block)."); ok = false; }
            else log("[OK] Worker can't connect to this machine (loopback blocked).");
        }
        finally { listener.Stop(); }

        // Outside: a public DNS server over TCP
        var o = await LinuxShell.RunAsync("sudo", new[] { "-n", "-u", worker, "timeout", "4", "bash", "-c", "exec 3<>/dev/tcp/1.1.1.1/53" }, ct);
        if (o.Ok) { log("[WARN] Worker CAN reach the internet. Run Provision (network block), unless network access is wanted."); ok = false; }
        else log("[OK] Worker can't reach the internet.");

        return ok;
    }

    private bool ValidatePaths(Action<string> log)
    {
        bool allOk = true;

        if (string.IsNullOrWhiteSpace(_general.ScriptSourcePath)) { log("[FAIL] Script Source Path is EMPTY."); allOk = false; }
        else if (!Directory.Exists(_general.ScriptSourcePath)) { log($"[FAIL] Script Directory NOT FOUND: {Full(_general.ScriptSourcePath)}"); allOk = false; }
        else log("[OK] Script Source Path verified.");

        if (string.IsNullOrWhiteSpace(_general.ResultsOutputPath)) { log("[FAIL] Results Output Path is EMPTY."); allOk = false; }
        else log("[OK] Results Output Path configured.");

        if (TryExecutor() is string exe) log($"[OK] Executor found: {exe}");
        else { log($"[FAIL] Executor NOT FOUND (expected '{ExecutorPaths.ExecutableName}' in {_python.PythonExecutablePath}).") ; allOk = false; }

        return allOk;
    }
}
