using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

using Common.Interfaces;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using ProviderlessModule.Code;
using ProviderlessModule.Configuration;
using ProviderlessModule.Configuration.Tunnel;
using ProviderlessModule.Infrastructure.Interfaces;
using ProviderlessModule.Services.Tunnel.Provisioning;

namespace ProviderlessModule.Services.Tunnel.Methods;

/// <summary>
/// Cloudflare tunnel provisioned through the provisioning Worker (Tools/cloudflare-provisioner).
/// On every start the node asks the Worker for its tunnel (idempotent: same tunnel, ingress updated to the current port),
/// stores the returned tunnel token and runs cloudflared with it. No Cloudflare API credentials on the node.
/// The subdomain is the node's Clinic ID (PortalAccessSettings:EnforcedPCIdentifier), so it stays stable and matches
/// the token-based Cloudflare tunnel; the Worker ties it to this node's key on first claim.
/// </summary>
public class CloudflareWorkerTunnelProvider : ITunnelProvider, IDisposable
{
    private readonly IOptionsMonitor<CloudflareWorkerSettings> _settings;
    private readonly ILogger<CloudflareWorkerTunnelProvider> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IProcessCaretaker _processCaretaker;
    private readonly CloudflareProvisioningClient _client;
    private readonly INodeProvisioningIdentity _identity;
    private readonly ILocalDataProvider _localData;
    private Process? _tunnelProcess;

    // The Worker lets another node take a subdomain whose owner hasn't provisioned for a while (CLAIM_TTL_DAYS, default 30),
    // so a running tunnel renews its claim periodically, not only on start.
    private static readonly TimeSpan ClaimRenewInterval = TimeSpan.FromHours(12);
    private CancellationTokenSource? _renewCts;

    private static readonly Regex LabelRegex = new("^[a-z0-9][a-z0-9-]{1,30}[a-z0-9]$", RegexOptions.CultureInvariant);

    public CloudflareWorkerTunnelProvider(
        IOptionsMonitor<CloudflareWorkerSettings> settings,
        ILogger<CloudflareWorkerTunnelProvider> logger,
        IServiceScopeFactory scopeFactory,
        IProcessCaretaker processCaretaker,
        CloudflareProvisioningClient client,
        INodeProvisioningIdentity identity,
        ILocalDataProvider localData)
    {
        _settings = settings;
        _logger = logger;
        _scopeFactory = scopeFactory;
        _processCaretaker = processCaretaker;
        _client = client;
        _identity = identity;
        _localData = localData;
    }

    public string Provider => TunnelProviderType.CloudflareWorker;
    public string CurrentBinaryPath => GetActiveConfig().BinaryPath;
    public string DownloadUrl => GetActiveConfig().DownloadUrl;
    public string? CurrentUrl { get; private set; }
    public bool IsRunning => _tunnelProcess != null && !_tunnelProcess.HasExited;
    public DateTime? LastStartTime { get; private set; }

    public async Task<string> StartTunnelAsync(int localPort, string scheme = "https", CancellationToken ct = default)
    {
        if (IsRunning) return CurrentUrl!;

        var cfg = _settings.CurrentValue;
        if (string.IsNullOrWhiteSpace(cfg.ServiceUrl))
        {
            throw new Exception("Cloudflare (Worker) tunnel: Provisioning Service URL is empty.");
        }

        string label = GetClinicLabel();
        var state = _identity.LoadState();
        string serviceOrigin = CloudflareProvisioningClient.GetServiceOrigin(cfg.ServiceUrl).ToString();
        bool sameService = state != null && string.Equals(state.ServiceUrl, serviceOrigin, StringComparison.OrdinalIgnoreCase);

        // Subdomain changed (e.g. from the old key-derived name to the Clinic ID): free the old one, start fresh
        if (sameService && !string.IsNullOrEmpty(state!.Label) && !string.Equals(state.Label, label, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                await _client.ReleaseAsync(cfg.ServiceUrl, cfg.JoinKey, state.Label, ct);
                _logger.LogInformation("Released the old subdomain {Old} (now using the Clinic ID {New}).", state.Label, label);
            }
            catch (ProvisioningServiceException ex)
            {
                _logger.LogWarning("Could not release the old subdomain {Old}: {Msg}", state.Label, ex.Message);
            }
            state = null;
            sameService = false;
        }

        bool rotate = cfg.TunnelRotationDays > 0
                      && sameService
                      && state!.TunnelCreatedUtc != default
                      && DateTime.UtcNow - state.TunnelCreatedUtc >= TimeSpan.FromDays(cfg.TunnelRotationDays);

        string token;
        string publicUrl;
        try
        {
            if (rotate) _logger.LogInformation("Tunnel is older than {Days} days, asking the provisioning service for a new one.", cfg.TunnelRotationDays);

            var result = await _client.ProvisionAsync(cfg.ServiceUrl, cfg.JoinKey, scheme, localPort, rotate, label, ct);

            if (string.IsNullOrEmpty(result.TunnelToken) || TryGetIdFromToken(result.TunnelToken) == null)
            {
                throw new Exception("Provisioning service returned no usable tunnel token.");
            }

            bool newTunnel = !sameService || state!.TunnelId != result.TunnelId || result.Rotated;
            _identity.SaveState(new ProvisioningState
            {
                ServiceUrl = serviceOrigin,
                Label = result.Label,
                Hostname = result.Hostname,
                Url = result.Url,
                TunnelId = result.TunnelId,
                TunnelCreatedUtc = newTunnel ? DateTime.UtcNow : state!.TunnelCreatedUtc,
                LastProvisionedUtc = DateTime.UtcNow
            });

            if (!string.Equals(result.TunnelToken, cfg.TunnelToken, StringComparison.Ordinal))
            {
                await SaveTokenAsync(result.TunnelToken);
            }

            _logger.LogInformation("Provisioning service assigned {Host} (tunnel {TunnelId}{Rotated}).",
                result.Hostname, result.TunnelId, result.Rotated ? ", rotated" : "");

            token = result.TunnelToken;
            publicUrl = result.Url;
        }
        catch (ProvisioningServiceException ex) when (ex.IsTransient
                                                       && sameService
                                                       && !string.IsNullOrEmpty(state!.Url)
                                                       && TryGetIdFromToken(cfg.TunnelToken) == state.TunnelId)
        {
            // Worker down: the stored token still runs the tunnel. Ingress keeps the port it had when last provisioned.
            _logger.LogWarning("{Msg} Starting with the stored tunnel token for {Url}.", ex.Message, state.Url);
            token = cfg.TunnelToken;
            publicUrl = state.Url;
        }

        // The token goes to cloudflared through TUNNEL_TOKEN, not the command line (visible in process lists).
        string args = $"tunnel --no-autoupdate run --url {scheme}://localhost:{localPort} --protocol http2";
        if (!string.IsNullOrEmpty(cfg.ExtraArgs)) args += $" {cfg.ExtraArgs}";

        string url = await LaunchProcessAsync(args, token, publicUrl, cfg.WaitTimeoutSeconds, ct);

        StartClaimRenewal(label, scheme, localPort, TryGetIdFromToken(token));
        return url;
    }

    private void StartClaimRenewal(string label, string scheme, int localPort, string? tunnelId)
    {
        _renewCts?.Cancel();
        var cts = new CancellationTokenSource();
        _renewCts = cts;
        _ = RenewClaimLoopAsync(label, scheme, localPort, tunnelId, cts.Token);
    }

    private async Task RenewClaimLoopAsync(string label, string scheme, int localPort, string? tunnelId, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(ClaimRenewInterval, ct);
                if (!IsRunning) return;

                var cfg = _settings.CurrentValue;
                try
                {
                    var result = await _client.ProvisionAsync(cfg.ServiceUrl, cfg.JoinKey, scheme, localPort, rotate: false, label, ct);
                    _logger.LogDebug("Renewed the claim on {Host}.", result.Hostname);

                    if (tunnelId != null && !string.Equals(result.TunnelId, tunnelId, StringComparison.OrdinalIgnoreCase))
                    {
                        // Our tunnel was replaced on the Worker side (evicted and re-claimed, deleted by hand...): restart with the new one
                        _logger.LogWarning("The Worker now has tunnel {New} for {Host} instead of {Old}. Restarting the tunnel.",
                            result.TunnelId, result.Hostname, tunnelId);
                        await StopTunnelAsync();
                        return;
                    }
                }
                catch (ProvisioningServiceException ex)
                {
                    // Transient: try again next interval. 409 means another node took the name (our claim expired): stop serving it.
                    _logger.LogWarning("Could not renew the claim on {Label}: {Msg}", label, ex.Message);
                    if (ex.Status == System.Net.HttpStatusCode.Conflict)
                    {
                        await StopTunnelAsync();
                        return;
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Claim renewal stopped for {Label}.", label);
        }
    }

    /// <summary>
    /// The Clinic ID as a DNS label. Tolerates an id saved with the domain appended ("abc123.processor-app.pp.ua").
    /// </summary>
    private string GetClinicLabel()
    {
        string raw = _localData.GetEncryptedMachineHash() ?? string.Empty;
        string label = raw.Trim().Split('.')[0].ToLowerInvariant();
        if (!LabelRegex.IsMatch(label))
        {
            throw new Exception($"Clinic ID '{raw}' can't be used as a subdomain (3-32 characters: a-z, 0-9, '-').");
        }
        return label;
    }

    private async Task<string> LaunchProcessAsync(string args, string tunnelToken, string publicUrl, int waitSeconds, CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = CurrentBinaryPath,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.EnvironmentVariables["TUNNEL_TRANSPORT_PROTOCOL"] = "http2";
        startInfo.EnvironmentVariables["TUNNEL_TOKEN"] = tunnelToken;

        _tunnelProcess = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var urlTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        DataReceivedEventHandler handler = async (s, e) =>
        {
            if (string.IsNullOrEmpty(e.Data)) return;
            _logger.LogDebug("Cloudflare (Worker): {Msg}", e.Data);

            // Token rejected (tunnel deleted, rotated or evicted): forget it so the next start asks the Worker again
            if (e.Data.Contains("Unauthorized") || e.Data.Contains("is not a valid tunnel"))
            {
                if (urlTcs.TrySetException(new Exception("Cloudflare rejected the tunnel token.")))
                {
                    _logger.LogCritical("Tunnel token rejected by Cloudflare. Clearing it; the next start re-provisions through the Worker.");
                    await SaveTokenAsync(null);
                }
            }

            if (e.Data.Contains("Connected") || e.Data.Contains("Registered tunnel connection"))
            {
                CurrentUrl = publicUrl;
                urlTcs.TrySetResult(publicUrl);
            }
        };

        _tunnelProcess.ErrorDataReceived += handler;
        _tunnelProcess.OutputDataReceived += handler;
        _tunnelProcess.Start();
        LastStartTime = DateTime.Now;
        _tunnelProcess.BeginErrorReadLine();
        _tunnelProcess.BeginOutputReadLine();

        _processCaretaker.EnforceParentalControl(_tunnelProcess);

        var timeout = TimeSpan.FromSeconds(Math.Clamp(waitSeconds, 10, 120));
        if (await Task.WhenAny(urlTcs.Task, Task.Delay(timeout, ct)) != urlTcs.Task)
        {
            await StopTunnelAsync();
            ct.ThrowIfCancellationRequested();
            throw new TimeoutException("Cloudflare (Worker) tunnel failed to connect.");
        }

        try
        {
            return await urlTcs.Task;
        }
        catch
        {
            await StopTunnelAsync();
            throw;
        }
    }

    private async Task SaveTokenAsync(string? token)
    {
        using var scope = _scopeFactory.CreateScope();
        var settingsService = scope.ServiceProvider.GetRequiredService<ISettingService>();
        string key = ConfigurationPathHelper.GetPath((CloudflareWorkerSettings s) => s.TunnelToken);
        await settingsService.SetAsync(ProviderlessModuleEntryPoint.MODULE_ID, key, token ?? "");
    }

    private static string? TryGetIdFromToken(string? token)
    {
        if (string.IsNullOrEmpty(token)) return null;
        try
        {
            var jsonBytes = Convert.FromBase64String(token);
            using var doc = JsonDocument.Parse(jsonBytes);
            return doc.RootElement.GetProperty("t").GetString();
        }
        catch { return null; }
    }

    public Task StopTunnelAsync()
    {
        StopTunnelInternal();
        return Task.CompletedTask;
    }

    private void StopTunnelInternal()
    {
        _renewCts?.Cancel();
        _renewCts = null;

        var process = _tunnelProcess;
        _tunnelProcess = null;
        CurrentUrl = null;
        if (process == null) return;

        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Cleanup note: {Msg}", ex.Message);
        }
        finally
        {
            process.Dispose();
        }
    }

    public void Dispose()
    {
        StopTunnelInternal();
    }

    private CloudflareSettings.BinaryPlatformConfig GetActiveConfig()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return _settings.CurrentValue.Windows;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return _settings.CurrentValue.Linux;
        throw new PlatformNotSupportedException();
    }
}
