using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

using ProcessingModule.Configuration;

namespace ProcessingModule.Services.Sandboxing.Linux;

/// <summary>
/// Linux OS-user sandbox names. Linux user/group names are lower case, so the configured names are lower-cased
/// ("Pr-User" -> "pr-user"). There is no separate "-rights" account and no stored password on Linux: the app switches to
/// the worker with <c>sudo -n -u &lt;worker&gt;</c> (rule installed once by processor-sandbox-setup.sh) and opens/closes job
/// folders itself through the sandbox group.
/// </summary>
public sealed record LinuxSandboxNames(string Worker, string Group)
{
    private static readonly Regex NameRule = new("^[a-z_][a-z0-9_-]{0,31}$", RegexOptions.Compiled);

    public static LinuxSandboxNames From(OsSandboxSettings settings)
    {
        string worker = (settings.UserName ?? string.Empty).Trim().ToLowerInvariant();
        string group = string.IsNullOrWhiteSpace(settings.GroupName) ? worker : settings.GroupName.Trim().ToLowerInvariant();

        if (!NameRule.IsMatch(worker))
            throw new InvalidOperationException(
                $"Processing user name '{settings.UserName}' is not a valid Linux user name (letters, digits, '-', '_'; max 32; starts with a letter).");
        if (!NameRule.IsMatch(group))
            throw new InvalidOperationException(
                $"Processing group name '{settings.GroupName}' is not a valid Linux group name.");
        if (worker == "root")
            throw new InvalidOperationException("The processing user can't be root.");

        return new LinuxSandboxNames(worker, group);
    }
}

/// <summary>Small process runner for the Linux tools the sandbox uses (sudo, id, chgrp, chmod, find, stat...).</summary>
internal static class LinuxShell
{
    public sealed record Result(int ExitCode, string StdOut, string StdErr)
    {
        public bool Ok => ExitCode == 0;
        public string Text => (StdOut + "\n" + StdErr).Trim();
    }

    public static async Task<Result> RunAsync(string file, IEnumerable<string> args, CancellationToken ct = default,
        string? workingDirectory = null, int timeoutSeconds = 60)
    {
        var psi = new ProcessStartInfo
        {
            FileName = file,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,   // closed right away: nothing may wait for a password on our stdin
            CreateNoWindow = true
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (workingDirectory != null) psi.WorkingDirectory = workingDirectory;

        using var process = new Process { StartInfo = psi };
        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return new Result(127, string.Empty, $"{file}: {ex.Message}");
        }
        process.StandardInput.Close();

        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* gone */ }
            if (ct.IsCancellationRequested) throw;
            return new Result(124, string.Empty, $"{file} timed out after {timeoutSeconds} s");
        }

        await Task.WhenAll(stdout, stderr);
        return new Result(process.ExitCode, stdout.Result, stderr.Result);
    }

    public static Task<Result> RunAsync(string file, params string[] args) => RunAsync(file, (IEnumerable<string>)args);

    /// <summary>Runs a command and hands every output line (stdout and stderr) to <paramref name="onLine"/> as it arrives.</summary>
    public static async Task<int> StreamAsync(string file, IEnumerable<string> args, Func<string, Task> onLine, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = file,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var process = new Process { StartInfo = psi };
        process.Start();
        process.StandardInput.Close();

        var gate = new SemaphoreSlim(1, 1);
        async Task Pump(StreamReader reader)
        {
            string? line;
            while ((line = await reader.ReadLineAsync()) != null)
            {
                await gate.WaitAsync(CancellationToken.None);
                try { await onLine(line); } finally { gate.Release(); }
            }
        }

        var pumps = Task.WhenAll(Pump(process.StandardOutput), Pump(process.StandardError));
        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* gone */ }
            throw;
        }
        await pumps;
        return process.ExitCode;
    }

    private static string? _appUser;
    private static string? _appGroup;

    /// <summary>The account this app runs as (owner of job folders).</summary>
    public static async Task<string> AppUserAsync()
        => _appUser ??= (await RunAsync("id", "-un")).StdOut.Trim();

    /// <summary>The app account's primary group (job folders go back to it when they are closed).</summary>
    public static async Task<string> AppGroupAsync()
        => _appGroup ??= (await RunAsync("id", "-gn")).StdOut.Trim();

    public static async Task<bool> IsRootAsync() => (await RunAsync("id", "-u")).StdOut.Trim() == "0";

    /// <summary>
    /// Is this process (not just the account) a member of the group? A membership added by the setup script only applies
    /// to processes started afterwards, so the app has to be restarted once after the first provisioning.
    /// </summary>
    public static async Task<bool> ProcessInGroupAsync(string group)
    {
        var entry = await RunAsync("getent", "group", group);
        if (!entry.Ok) return false;
        var parts = entry.StdOut.Trim().Split(':');
        if (parts.Length < 3) return false;
        string gid = parts[2];

        foreach (var line in await File.ReadAllLinesAsync("/proc/self/status"))
        {
            if (line.StartsWith("Gid:", StringComparison.Ordinal) && line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Skip(1).Contains(gid))
                return true;
            if (line.StartsWith("Groups:", StringComparison.Ordinal))
                return line.Substring(7).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Contains(gid);
        }
        return false;
    }

    /// <summary>Single-quotes a value for a command line the admin copies into a terminal.</summary>
    public static string Quote(string value)
        => "'" + value.Replace("'", "'\\''") + "'";

    public static string CommandLine(string file, IEnumerable<string> args)
    {
        var sb = new StringBuilder(file);
        foreach (var a in args) sb.Append(' ').Append(Quote(a));
        return sb.ToString();
    }
}
