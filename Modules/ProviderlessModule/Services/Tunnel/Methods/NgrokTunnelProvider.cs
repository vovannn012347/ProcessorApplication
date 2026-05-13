namespace ProviderlessModule.Services.Tunnel.Methods;

/*
public class NgrokTunnelProvider : ITunnelProvider
{
    private readonly IOptionsMonitor<NgrokSettings> _settings;
    private Process? _process;
    private DateTime? _lastStartTime;
    public NgrokTunnelProvider(IOptionsMonitor<NgrokSettings> settings) => _settings = settings;

    public TunnelProviderType Provider => TunnelProviderType.Ngrok;
    public string? CurrentUrl { get; private set; }

    // Logic updated to match your multi-platform requirement
    public string CurrentBinaryPath => RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
        ? _settings.CurrentValue.Windows.BinaryPath
        : _settings.CurrentValue.Linux.BinaryPath;

    public string DownloadUrl => RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
        ? _settings.CurrentValue.Windows.DownloadUrl
        : _settings.CurrentValue.Linux.DownloadUrl;

    public bool IsRunning => _process != null && !_process.HasExited;
    public DateTime? LastStartTime => _lastStartTime;

    public async Task<string> StartTunnelAsync(int localPort, string scheme, CancellationToken ct = default)
    {
        if (IsRunning) return CurrentUrl!;

        var cfg = _settings.CurrentValue;

        // 1. Build Arguments for 2026 Static Domain logic
        // We use the --url flag to bind to the doctor's specific free domain
        string args = $"http {localPort} --log=stdout";

        if (!string.IsNullOrEmpty(cfg.CustomDomain))
        {
            args += $" --url {cfg.CustomDomain}";
        }
        else if (!string.IsNullOrEmpty(cfg.Region))
        {
            args += $" --region {cfg.Region}";
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = Path.GetFullPath(CurrentBinaryPath),
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(CurrentBinaryPath)
        };

        // Set AuthToken via Environment variable for security
        startInfo.EnvironmentVariables["NGROK_AUTHTOKEN"] = cfg.AuthToken;

        _process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var urlTcs = new TaskCompletionSource<string>();
        _lastStartTime = DateTime.Now;

        // 2. Robust Output Scraping
        _process.OutputDataReceived += (s, e) =>
        {
            if (string.IsNullOrEmpty(e.Data)) return;

            // Regex captures the https URL assigned by Ngrok
            var match = Regex.Match(e.Data, @"url=(https://[a-zA-Z0-9-\.]+(:[0-9]+)?)");
            if (match.Success)
            {
                CurrentUrl = match.Groups[1].Value;
                urlTcs.TrySetResult(CurrentUrl);
            }
        };

        _process.Start();
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        // 3. Timeout/Success Logic
        var completedTask = await Task.WhenAny(urlTcs.Task, Task.Delay(TimeSpan.FromSeconds(20), ct));
        if (completedTask != urlTcs.Task)
        {
            await StopTunnelAsync();
            throw new TimeoutException("Ngrok failed to secure a tunnel URL. Verify the AuthToken and Static Domain.");
        }

        return await urlTcs.Task;
    }

    public Task StopTunnelAsync()
    {
        if (_process != null && !_process.HasExited)
        {
            _process.Kill(true);
            _process.Dispose();
            _process = null;
        }

        CurrentUrl = null;
        _lastStartTime = null;
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        StopTunnelAsync().Wait();
    }
}
*/