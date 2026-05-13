using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

using Amazon.Runtime.Internal.Endpoints.StandardLibrary;

using Common.Interfaces;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Org.BouncyCastle.Asn1.Ocsp;

using ProviderlessModule.Code;
using ProviderlessModule.Configuration;
using ProviderlessModule.Configuration.Tunnel;
using ProviderlessModule.Infrastructure.Interfaces;

namespace ProviderlessModule.Services.Tunnel.Methods;
public class CloudflareTunnelProvider : ITunnelProvider, IDisposable
{
    private readonly IOptionsMonitor<CloudflareSettings> _settings;
    private readonly ILogger<ITunnelProvider> _logger;
    private readonly HttpClient _http;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IProcessCaretaker _processCaretaker;
    private readonly ILocalDataProvider _localDataProvider;
    private Process? _tunnelProcess;

    public CloudflareTunnelProvider(
        IOptionsMonitor<CloudflareSettings> settings,
        ILogger<ITunnelProvider> logger,
        IHttpClientFactory http,
        IServiceScopeFactory scopeFactory,
        IProcessCaretaker processCaretaker,
        ILocalDataProvider localDataProvider)
    {
        _settings = settings;
        _logger = logger;
        _scopeFactory = scopeFactory;
        _processCaretaker = processCaretaker;
        _localDataProvider = localDataProvider;

        _http = http.CreateClient();
        _http.BaseAddress = new Uri("https://api.cloudflare.com/client/v4/");
    }

    public string Provider => TunnelProviderType.Cloudflare;
    public string CurrentBinaryPath => GetActiveConfig().BinaryPath;
    public string DownloadUrl => GetActiveConfig().DownloadUrl;
    public string? CurrentUrl { get; private set; }
    public bool IsRunning => _tunnelProcess != null && !_tunnelProcess.HasExited;
    public DateTime? LastStartTime { get; private set; }

    public async Task<string> StartTunnelAsync(int localPort, string scheme, CancellationToken ct = default)
    {
        if (IsRunning) return CurrentUrl!;

        var cfg = _settings.CurrentValue;
        string machineHash = _localDataProvider.GetEncryptedMachineHash();
        string? executionToken = cfg.TunnelToken;
        string? targetUrl = cfg.CustomPublicUrl;

        bool hasProvisioner = !string.IsNullOrEmpty(cfg.ProvisionToken) &&
                              !string.IsNullOrEmpty(cfg.AccountId) &&
                              !string.IsNullOrEmpty(cfg.ZoneId);

        // STEP 1: Structural & API Validation
        string? tunnelId = TryGetIdFromToken(executionToken);
        if (string.IsNullOrEmpty(tunnelId))
        {
            if (!string.IsNullOrEmpty(executionToken)) _logger.LogWarning("Local token is structurally invalid.");
            executionToken = null;
        }

        if (executionToken == null && !hasProvisioner)
        {
            throw new Exception("Cloudflare setup incomplete. Provide a TunnelToken or full Provisioning API details.");
        }

        // STEP 2: Usability Sync
        if (executionToken != null && hasProvisioner)
        {
            bool isUsable = await IsTokenUsableAsync(executionToken, machineHash, localPort, scheme, ct);
            if (!isUsable)
            {
                _logger.LogWarning("Existing Tunnel Token is stale (mismatched ID). Forcing re-provisioning...");
                executionToken = null;
            }
        }

        // STEP 3: Provision or Sync Port
        if (string.IsNullOrEmpty(executionToken))
        {
            _logger.LogInformation("Orchestrating Cloudflare infrastructure for machine: {Hash}", machineHash);
            var (token, url) = await OrchestrateInfrastructureAsync(machineHash, localPort, scheme, ct);
            executionToken = token;
            targetUrl = url;
            await SaveTokenToRegistry(token);
        }
        else 
        if (hasProvisioner)
        {
            // Even if token is valid, we MUST update Ingress Rules in case the local port changed
            _logger.LogInformation("Syncing existing tunnel config to port {Port}...", localPort);
            string tid = TryGetIdFromToken(executionToken)!;
            string subdomain = $"{machineHash}";
            string fullHostname = $"{subdomain}.processor-app.pp.ua";

            await ApplyTunnelConfigurationAsync(tid, fullHostname, localPort, scheme, ct);
            await UpsertDnsRecordAsync(subdomain, tid, ct);
            targetUrl = $"https://{fullHostname}";
        }

        // STEP 4: Launch
        string args = $"tunnel --no-autoupdate run --token {executionToken} --url {scheme}://localhost:{localPort} --protocol http2";
        if (!string.IsNullOrEmpty(cfg.ExtraArgs)) args += $" {cfg.ExtraArgs}";

        return await LaunchProcessInternalAsync(args, targetUrl, machineHash, ct);
    }

    private string? TryGetIdFromToken(string? token)
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

    private async Task<T?> SendAsync<T>(
    HttpMethod method,
    string url,
    string? bearerToken = null,
    object? body = null,
    CancellationToken CancelToken = default)
    {
        using var request = new HttpRequestMessage(method, url);

        // Per-request auth (safe for singleton)
        if (!string.IsNullOrEmpty(bearerToken))
        {
            request.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", bearerToken);
        }

        // Optional JSON body
        if (body != null)
        {
            request.Content = JsonContent.Create(body);
        }

        using var response = await _http.SendAsync(request, CancelToken);

        // Throw on non-success (you can customize this)
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(CancelToken);
            throw new HttpRequestException(
                $"Request failed ({(int)response.StatusCode}): {error}");
        }

        // No content case
        if (response.Content.Headers.ContentLength == 0)
            return default;

        // Deserialize JSON
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: CancelToken);
    }

    private async Task<bool> IsTokenUsableAsync(string localToken, string hash, int port, string scheme, CancellationToken ct)
    {
        try
        {
            string? localTunnelId = TryGetIdFromToken(localToken);
            if (localTunnelId == null) return false;

            var cfg = _settings.CurrentValue;
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", cfg.ProvisionToken);

            // A. Identity Check: Confirm the tunnel name still matches this ID on CF
            var list = await _http.GetFromJsonAsync<CloudflareResponse<List<TunnelResult>>>(
                $"accounts/{cfg.AccountId}/cfd_tunnel?name=Clinic_{hash}", ct);

            if (list?.Result?.FirstOrDefault()?.Id != localTunnelId) return false;

            // B. Configuration Check: Confirm the Hostname and Port/Scheme are correct
            var configRes = await _http.GetFromJsonAsync<CloudflareResponse<TunnelConfigurationRequest>>(
                $"accounts/{cfg.AccountId}/cfd_tunnel/{localTunnelId}/configurations", ct);

            string expectedHost = $"{hash}.processor-app.pp.ua";
            string expectedService = $"{scheme}://localhost:{port}";

            // Verify that our specific hostname points to exactly our current local port
            bool isConfigCorrect = configRes?.Result?.Config?.Ingress?.Any(r =>
                r.Hostname == expectedHost &&
                r.Service.TrimEnd('/') == expectedService.TrimEnd('/')) ?? false;

            return isConfigCorrect;
        }
        catch { return false; }
    }

    private async Task SaveTokenToRegistry(string? token)
    {
        using var scope = _scopeFactory.CreateScope();
        var settingsService = scope.ServiceProvider.GetRequiredService<ISettingService>();
        string key = ConfigurationPathHelper.GetPath((CloudflareSettings s) => s.TunnelToken);
        await settingsService.SetAsync(ProviderlessModule.MODULE_ID, key, token ?? "");
    }

    private async Task<(string Token, string PublicUrl)> OrchestrateInfrastructureAsync(string hash, int port, string scheme, CancellationToken ct)
    {
        var cfg = _settings.CurrentValue;

        string tunnelName = $"Clinic_{hash}";
        string subdomain = $"{hash}";
        string fullHostname = $"{subdomain}.processor-app.pp.ua";

        // A. Delete existing
        
        //var listRequest = new HttpRequestMessage(HttpMethod.Get, );
        //listRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", );
        //var response = await _http.SendAsync(listRequest, ct);
        var list = await SendAsync<CloudflareResponse<List<TunnelResult>>>(
            HttpMethod.Get,
            $"accounts/{cfg.AccountId}/cfd_tunnel?name={tunnelName}",
            cfg.ProvisionToken, CancelToken: ct);
            //await response.Content.ReadFromJsonAsync<>(cancellationToken: ct);
        if (list?.Result != null)
        {
            foreach (var t in list.Result) await _http.DeleteAsync($"accounts/{cfg.AccountId}/cfd_tunnel/{t.Id}", ct);
        }

        // B. Create Tunnel
        var createRes = await _http.PostAsJsonAsync($"accounts/{cfg.AccountId}/cfd_tunnel", new { name = tunnelName }, ct);
        var content = await createRes.Content.ReadFromJsonAsync<CloudflareResponse<TunnelResult>>(cancellationToken: ct);
        if (content?.Result?.Token == null) throw new Exception("API failed to return a Tunnel Token.");

        // C. Configure Ingress & D. DNS
        await ApplyTunnelConfigurationAsync(content.Result.Id, fullHostname, port, scheme, ct);
        await UpsertDnsRecordAsync(subdomain, content.Result.Id, ct);

        return (content.Result.Token, $"https://{fullHostname}");
    }

    private async Task ApplyTunnelConfigurationAsync(string tunnelId, string fullHostname, int port, string scheme, CancellationToken ct)
    {
        var cfg = _settings.CurrentValue;
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", cfg.ProvisionToken);

        var configBody = new TunnelConfigurationRequest
        {
            Config = new TunnelConfig
            {
                Ingress = new List<TunnelIngressRule> {
                    new() { Hostname = fullHostname, Service = $"{scheme}://localhost:{port}" },
                    new() { Service = "http_status:404" }
                }
            }
        };
        await _http.PutAsJsonAsync($"accounts/{cfg.AccountId}/cfd_tunnel/{tunnelId}/configurations", configBody, ct);
    }

    private async Task UpsertDnsRecordAsync(string subdomain, string tunnelId, CancellationToken ct)
    {
        var cfg = _settings.CurrentValue;
        string fullHostname = $"{subdomain}.processor-app.pp.ua";
        string target = $"{tunnelId}.cfargotunnel.com";

        var search = await _http.GetFromJsonAsync<CloudflareResponse<List<DnsRecordResult>>>($"zones/{cfg.ZoneId}/dns_records?name={fullHostname}", ct);
        var existing = search?.Result?.FirstOrDefault();
        var body = new { type = "CNAME", name = subdomain, content = target, proxied = true, comment = $"HB:{DateTime.UtcNow:O}" };

        if (existing == null) await _http.PostAsJsonAsync($"zones/{cfg.ZoneId}/dns_records", body, ct);
        else if (existing.Content != target) await _http.PutAsJsonAsync($"zones/{cfg.ZoneId}/dns_records/{existing.Id}", body, ct);
    }

    private async Task<string> LaunchProcessInternalAsync(string args, string? targetUrl, string hash, CancellationToken ct)
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

        _tunnelProcess = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var urlTcs = new TaskCompletionSource<string>();

        DataReceivedEventHandler handler = async (s, e) => {
            if (string.IsNullOrEmpty(e.Data)) return;
            _logger.LogDebug("Cloudflare: {Msg}", e.Data);

            // AUTH TRAP: If token is revoked, wipe settings so we re-provision on next try
            if (e.Data.Contains("Unauthorized") || e.Data.Contains("is not a valid tunnel"))
            {
                _logger.LogCritical("Token rejected by Cloudflare! Clearing local registry.");
                await SaveTokenToRegistry(null);
                urlTcs.TrySetException(new Exception("Cloudflare Authentication Failed."));
            }

            if (e.Data.Contains("Connected") || e.Data.Contains("Registered tunnel connection"))
            {
                CurrentUrl = targetUrl?.Replace("{hash}", hash) ?? "NAMED_TUNNEL_ACTIVE";
                urlTcs.TrySetResult(CurrentUrl);
            }
        };

        _tunnelProcess.ErrorDataReceived += handler;
        _tunnelProcess.OutputDataReceived += handler;
        _tunnelProcess.Start();
        LastStartTime = DateTime.Now;
        _tunnelProcess.BeginErrorReadLine();
        _tunnelProcess.BeginOutputReadLine();

        _processCaretaker.EnforceParentalControl(_tunnelProcess);

        if (await Task.WhenAny(urlTcs.Task, Task.Delay(TimeSpan.FromSeconds(45), ct)) != urlTcs.Task)
        {
            await StopTunnelAsync();
            throw new TimeoutException("Cloudflare failed to connect.");
        }
        return await urlTcs.Task;
    }

    public void Dispose()
    {
        _logger.LogInformation("Server shutting down. Cleaning up tunnel process...");
        StopTunnelInternal();
    }
    private void StopTunnelInternal()
    {
        if (_tunnelProcess != null && !_tunnelProcess.HasExited)
        {
            try
            {
                // The 'true' flag kills the process AND all its children (entire tree)
                _tunnelProcess.Kill(entireProcessTree: true);
                _tunnelProcess.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogDebug("Cleanup note: {Msg}", ex.Message);
            }
            finally
            {
                _tunnelProcess = null;
                CurrentUrl = null;
            }
        }
    }

    public Task StopTunnelAsync()
    {
        if (_tunnelProcess != null && !_tunnelProcess.HasExited)
        {
            try { _tunnelProcess.Kill(true); } catch { }
            _tunnelProcess.Dispose();
            _tunnelProcess = null;
        }
        CurrentUrl = null;
        return Task.CompletedTask;
    }

    private CloudflareSettings.BinaryPlatformConfig GetActiveConfig()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return _settings.CurrentValue.Windows;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return _settings.CurrentValue.Linux;
        throw new PlatformNotSupportedException();
    }

    // DTOs
    private record CloudflareResponse<T>(T Result, bool Success);
    private record TunnelResult(string Id, string Name, string? Token);
    public record DnsRecordResult([property: JsonPropertyName("id")] string Id, [property: JsonPropertyName("content")] string Content);
    public class TunnelConfigurationRequest { [JsonPropertyName("config")] public TunnelConfig Config { get; set; } = new(); }
    public class TunnelConfig { [JsonPropertyName("ingress")] public List<TunnelIngressRule> Ingress { get; set; } = new(); }
    public class TunnelIngressRule
    {
        [JsonPropertyName("hostname")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Hostname { get; set; }
        [JsonPropertyName("service")] public string Service { get; set; } = string.Empty;
    }
}
