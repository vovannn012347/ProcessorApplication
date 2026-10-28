using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.Extensions.Logging;

namespace ProviderlessModule.Services.Tunnel.Provisioning;

/// <summary>
/// The node's own provisioning key (ECDSA P-256). The provisioning Worker derives the node's
/// subdomain from the public key, so only the holder of this key can manage that subdomain.
/// Also keeps the last provisioning result so the node can start from it if the Worker is unreachable.
/// </summary>
public interface INodeProvisioningIdentity
{
    /// <summary>DER SubjectPublicKeyInfo of the node key (created on first use).</summary>
    byte[] PublicKeySpki { get; }

    /// <summary>26-character DNS label the Worker gives this node: base32(SHA-256(SPKI)[0..16]).</summary>
    string Label { get; }

    /// <summary>ECDSA-SHA256 signature in IEEE P1363 format (r||s), as WebCrypto expects.</summary>
    byte[] Sign(byte[] data);

    ProvisioningState? LoadState();
    void SaveState(ProvisioningState state);
}

public sealed class ProvisioningState
{
    public string ServiceUrl { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string Hostname { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string TunnelId { get; set; } = string.Empty;

    /// <summary>When the current tunnel was created (first provision or last rotation), UTC.</summary>
    public DateTime TunnelCreatedUtc { get; set; }

    public DateTime LastProvisionedUtc { get; set; }
}

public sealed class NodeProvisioningIdentity : INodeProvisioningIdentity, IDisposable
{
    private const string KeyFile = "./Module_Data/Portal/keys/provisioning_node.key";
    private const string StateFile = "./Module_Data/Portal/keys/provisioning_state.json";
    private const string Base32Alphabet = "abcdefghijklmnopqrstuvwxyz234567";

    private readonly ILogger<NodeProvisioningIdentity> _logger;
    private readonly object _lock = new();
    private ECDsa? _key;
    private byte[]? _spki;
    private string? _label;

    public NodeProvisioningIdentity(ILogger<NodeProvisioningIdentity> logger)
    {
        _logger = logger;
    }

    public byte[] PublicKeySpki
    {
        get { EnsureKey(); return _spki!; }
    }

    public string Label
    {
        get { EnsureKey(); return _label!; }
    }

    public byte[] Sign(byte[] data)
    {
        EnsureKey();
        lock (_lock)
        {
            // .NET's default ECDSA signature format is IEEE P1363 (r||s), the format WebCrypto verifies.
            return _key!.SignData(data, HashAlgorithmName.SHA256);
        }
    }

    public ProvisioningState? LoadState()
    {
        try
        {
            string path = Path.GetFullPath(StateFile);
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<ProvisioningState>(File.ReadAllText(path));
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Could not read the provisioning state: {Msg}", ex.Message);
            return null;
        }
    }

    public void SaveState(ProvisioningState state)
    {
        string path = Path.GetFullPath(StateFile);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        WriteAtomically(path, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
    }

    private void EnsureKey()
    {
        if (_key != null) return;
        lock (_lock)
        {
            if (_key != null) return;

            string path = Path.GetFullPath(KeyFile);
            var key = ECDsa.Create();
            if (File.Exists(path))
            {
                key.ImportFromPem(File.ReadAllText(path));
                if (key.KeySize != 256)
                {
                    throw new CryptographicException($"Provisioning key {path} is not a P-256 key.");
                }
            }
            else
            {
                key.GenerateKey(ECCurve.NamedCurves.nistP256);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                WriteAtomically(path, ToPem("PRIVATE KEY", key.ExportPkcs8PrivateKey()));
                _logger.LogInformation("Created the tunnel provisioning key at {Path}. Back it up: the node's subdomain is tied to it.", path);
            }

            _spki = key.ExportSubjectPublicKeyInfo();
            _label = Base32(SHA256.HashData(_spki).AsSpan(0, 16));
            _key = key;
        }
    }

    private static void WriteAtomically(string path, string content)
    {
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, content, new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }

    private static string ToPem(string label, byte[] der)
    {
        var sb = new StringBuilder();
        sb.Append("-----BEGIN ").Append(label).Append("-----\n");
        string b64 = Convert.ToBase64String(der);
        for (int i = 0; i < b64.Length; i += 64)
        {
            sb.Append(b64, i, Math.Min(64, b64.Length - i)).Append('\n');
        }
        sb.Append("-----END ").Append(label).Append("-----\n");
        return sb.ToString();
    }

    internal static string Base32(ReadOnlySpan<byte> bytes)
    {
        var sb = new StringBuilder((bytes.Length * 8 + 4) / 5);
        int buffer = 0, bits = 0;
        foreach (byte b in bytes)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                sb.Append(Base32Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
            buffer &= (1 << bits) - 1;
        }
        if (bits > 0) sb.Append(Base32Alphabet[(buffer << (5 - bits)) & 31]);
        return sb.ToString();
    }

    public void Dispose() => _key?.Dispose();
}

public sealed class ProvisioningServiceException : Exception
{
    public ProvisioningServiceException(string message, HttpStatusCode? status, bool isTransient, Exception? inner = null)
        : base(message, inner)
    {
        Status = status;
        IsTransient = isTransient;
    }

    public HttpStatusCode? Status { get; }

    /// <summary>True when the Worker couldn't be reached or failed on its side (worth falling back to a stored token).</summary>
    public bool IsTransient { get; }
}

public sealed class ProvisioningResult
{
    [JsonPropertyName("label")] public string Label { get; set; } = string.Empty;
    [JsonPropertyName("hostname")] public string Hostname { get; set; } = string.Empty;
    [JsonPropertyName("url")] public string Url { get; set; } = string.Empty;
    [JsonPropertyName("tunnelId")] public string TunnelId { get; set; } = string.Empty;
    [JsonPropertyName("tunnelName")] public string TunnelName { get; set; } = string.Empty;
    [JsonPropertyName("tunnelToken")] public string TunnelToken { get; set; } = string.Empty;
    [JsonPropertyName("rotated")] public bool Rotated { get; set; }
}

/// <summary>
/// Talks to the provisioning Worker (Tools/cloudflare-provisioner). Every request is signed with the node key:
/// "PNODE-V1\nPOST\n&lt;path&gt;\n&lt;unix seconds&gt;\n&lt;nonce&gt;\n&lt;hex sha256 of body&gt;".
/// </summary>
public sealed class CloudflareProvisioningClient
{
    private const string SignatureScheme = "PNODE-V1";

    private readonly IHttpClientFactory _httpFactory;
    private readonly INodeProvisioningIdentity _identity;
    private readonly ILogger<CloudflareProvisioningClient> _logger;

    public CloudflareProvisioningClient(
        IHttpClientFactory httpFactory,
        INodeProvisioningIdentity identity,
        ILogger<CloudflareProvisioningClient> logger)
    {
        _httpFactory = httpFactory;
        _identity = identity;
        _logger = logger;
    }

    public Task<ProvisioningResult> ProvisionAsync(
        string serviceUrl, string? joinKey, string scheme, int port, bool rotate, CancellationToken ct)
    {
        var body = new { scheme, port, rotate };
        return SendAsync<ProvisioningResult>(serviceUrl, "/v1/provision", joinKey, body, ct);
    }

    public Task ReleaseAsync(string serviceUrl, string? joinKey, CancellationToken ct)
    {
        return SendAsync<JsonElement>(serviceUrl, "/v1/release", joinKey, new { }, ct);
    }

    public static Uri GetServiceOrigin(string serviceUrl)
    {
        if (!Uri.TryCreate(serviceUrl?.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            throw new ProvisioningServiceException(
                $"Provisioning service URL '{serviceUrl}' is not a valid http(s) URL.", null, isTransient: false);
        }

        // The Worker returns the tunnel token, so only allow plain http for a local test Worker (wrangler dev).
        if (uri.Scheme == Uri.UriSchemeHttp && !uri.IsLoopback)
        {
            throw new ProvisioningServiceException(
                "Provisioning service URL must use https (http is allowed for localhost only).", null, isTransient: false);
        }

        return new Uri(uri.GetLeftPart(UriPartial.Authority) + "/");
    }

    private async Task<T> SendAsync<T>(string serviceUrl, string path, string? joinKey, object body, CancellationToken ct)
    {
        Uri target = new(GetServiceOrigin(serviceUrl), path);

        byte[] bodyBytes = JsonSerializer.SerializeToUtf8Bytes(body);
        string timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        string nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18)).Replace('+', '-').Replace('/', '_');
        string bodyHash = Convert.ToHexString(SHA256.HashData(bodyBytes)).ToLowerInvariant();
        string canonical = string.Join("\n", SignatureScheme, "POST", target.AbsolutePath, timestamp, nonce, bodyHash);
        byte[] signature = _identity.Sign(Encoding.UTF8.GetBytes(canonical));

        using var request = new HttpRequestMessage(HttpMethod.Post, target);
        request.Content = new ByteArrayContent(bodyBytes);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.Add("X-Node-Key", Convert.ToBase64String(_identity.PublicKeySpki));
        request.Headers.Add("X-Timestamp", timestamp);
        request.Headers.Add("X-Nonce", nonce);
        request.Headers.Add("X-Signature", Convert.ToBase64String(signature));
        if (!string.IsNullOrEmpty(joinKey))
        {
            request.Headers.Add("X-Join-Key", joinKey);
        }

        var http = _httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(60);

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            throw new ProvisioningServiceException(
                $"Provisioning service {target.Host} is unreachable: {ex.Message}", null, isTransient: true, ex);
        }

        using (response)
        {
            string text = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                string error = TryReadError(text) ?? $"HTTP {(int)response.StatusCode}";
                int code = (int)response.StatusCode;
                bool transient = code >= 500 || code == 429 || code == 408;
                if (code == 401 && error.Contains("clock", StringComparison.OrdinalIgnoreCase))
                {
                    error += ". Check this PC's date and time.";
                }
                throw new ProvisioningServiceException(
                    $"Provisioning service refused {path} ({code}): {error}", response.StatusCode, transient);
            }

            try
            {
                T? result = JsonSerializer.Deserialize<T>(text);
                if (result is null) throw new JsonException("empty response");
                return result;
            }
            catch (JsonException ex)
            {
                throw new ProvisioningServiceException(
                    $"Provisioning service returned an unreadable response for {path}.", response.StatusCode, isTransient: true, ex);
            }
        }
    }

    private static string? TryReadError(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.TryGetProperty("error", out var e) ? e.GetString() : null;
        }
        catch
        {
            return null;
        }
    }
}
