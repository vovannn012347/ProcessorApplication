using System.ComponentModel.DataAnnotations;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using ProviderlessModule.Code;
using ProviderlessModule.Configuration;
using ProviderlessModule.Configuration.Registry;
using ProviderlessModule.Infrastructure.Interfaces;

namespace ProviderlessModule.Services.Registry.Methods;

public class GitHubRegistry : IUrlRegistry
{
    private readonly HttpClient _http;
    private readonly ILocalDataProvider _localData;
    private readonly IOptionsMonitor<GithubSettings> _gitSettings;
    private readonly ILogger<GitHubRegistry> _logger;
    protected bool _active = false;

    public GitHubRegistry(
        HttpClient http,
        ILocalDataProvider localData,
        IOptionsMonitor<GithubSettings> gitSettings,
        ILogger<GitHubRegistry> logger)
    {
        _http = http;
        _localData = localData;
        _gitSettings = gitSettings;
        _logger = logger;

        _http.BaseAddress = new Uri(_gitSettings.CurrentValue.GitHubUrl);
        _http.DefaultRequestHeaders.Add("User-Agent", "MedicalPortal-Orchestrator");
    }

    public string Provider => RegistryProviderType.GitHub;

    public bool IsActive { get => _active; set => _active = value; }

    public async Task RegisterAccessAsync(string tunnelUrl, CancellationToken ct)
    {
        var git = _gitSettings.CurrentValue;
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", git.GitHubToken);

        string clinicId = _localData.GetRegistryAlias();
        string secret = _localData.GetSharedSecret();

        string path = $"{git.RegistryPath}/{clinicId}.json";
        string apiUrl = $"repos/{git.RepositoryName}/contents/{path}";

        string? sha = null;
        bool needsUpdate = true;

        // 1. Fetch remote state for Idempotency & Heartbeat check
        var getRes = await _http.GetAsync($"{apiUrl}?ref={git.Branch}", ct);
        if (getRes.IsSuccessStatusCode)
        {
            var remoteFile = await getRes.Content.ReadFromJsonAsync<GithubContentDto>(cancellationToken: ct);
            sha = remoteFile?.Sha;

            if (!string.IsNullOrEmpty(remoteFile?.Content))
            {
                var jsonBytes = Convert.FromBase64String(remoteFile.Content);
                var remoteData = JsonSerializer.Deserialize<RegistryData>(Encoding.UTF8.GetString(jsonBytes));

                string decryptedRemoteUrl = SimpleCrypto.Decrypt(remoteData?.Url ?? "", secret);

                // Logic: Only update if URL changed OR heartbeat is stale
                bool urlMatches = decryptedRemoteUrl == tunnelUrl;
                bool heartbeatFresh = remoteData != null &&
                                     (DateTime.UtcNow - remoteData.UpdatedAt).TotalHours < git.ForceUpdateIntervalHours;

                if (urlMatches && heartbeatFresh)
                {
                    //_logger.LogInformation("Registry for Clinic {Id} is fresh. Skipping update.", clinicId);
                    needsUpdate = false;
                }
            }
        }

        if (!needsUpdate)
        {
            this.IsActive = true;
            return;
        }

        IsActive = false;

        // 2. Encrypt and Push
        string encryptedUrl = SimpleCrypto.Encrypt(tunnelUrl, secret);
        var payload = new
        {
            message = $"Update clinic {clinicId} heartbeat [System-Generated]",
            content = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new RegistryData(encryptedUrl, DateTime.UtcNow)))),
            branch = git.Branch,
            sha
        };

        var putRes = await _http.PutAsJsonAsync(apiUrl, payload, ct);
        if (!putRes.IsSuccessStatusCode)
        {
            var error = await putRes.Content.ReadAsStringAsync(ct);
            _logger.LogError("GitHub Registry update failed: {Error}", error);
            throw new Exception("Could not update GitHub data repository.");
        }

        this.IsActive = true;

        _logger.LogInformation("Successfully updated registry for Clinic {Id}", clinicId);
    }

    public string GetQrDiscoveryUrl()
    {
        var git = _gitSettings.CurrentValue;
        string clinicId = _localData.GetRegistryAlias();
        string secret = _localData.GetSharedSecret();

        // Standardized format: SiteURL + # + ID + : + Secret
        // Ensure ResolverSiteUrl ends with a slash if not already present
        string baseUrl = git.ResolverSiteUrl.EndsWith("/") ? git.ResolverSiteUrl : git.ResolverSiteUrl + "/";

        return $"{baseUrl}#{clinicId}:{secret}";
    }

    private record GithubContentDto(
        [property: JsonPropertyName("sha")] string Sha,
        [property: JsonPropertyName("content")] string Content);

    private record RegistryData(
        [property: JsonPropertyName("url")] string Url,
        [property: JsonPropertyName("updatedAt")] DateTime UpdatedAt);
}