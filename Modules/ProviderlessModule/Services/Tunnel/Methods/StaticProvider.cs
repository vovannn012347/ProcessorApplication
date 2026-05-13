using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

using Microsoft.Extensions.Options;

using ProviderlessModule.Configuration;
using ProviderlessModule.Configuration.Tunnel;
using ProviderlessModule.Infrastructure.Interfaces;

namespace ProviderlessModule.Services.Tunnel.Methods;

//when server is public and statically cnfigured
/// <summary>
/// Used when the server is public and statically configured.
/// No background process is launched; it simply returns the pre-configured URL.
/// </summary>
public class StaticProvider : ITunnelProvider
{
    private readonly IOptionsMonitor<StaticUrlSettings> _settings;

    public StaticProvider(IOptionsMonitor<StaticUrlSettings> settings)
    {
        _settings = settings;
    }

    public string Provider => TunnelProviderType.Static;

    // Returns the static URL from settings if it exists
    public string? CurrentUrl => _settings.CurrentValue.PublicUrl;

    // No binary required for a static setup
    public string CurrentBinaryPath => string.Empty;
    public string DownloadUrl => string.Empty;

    // Logically "Running" as soon as a valid URL is provided
    public bool IsRunning => !string.IsNullOrEmpty(CurrentUrl);

    public DateTime? LastStartTime { get; private set; }

    public Task<string> StartTunnelAsync(int localPort, string scheme, CancellationToken ct = default)
    {
        var cfg = _settings.CurrentValue;

        if (string.IsNullOrWhiteSpace(cfg.PublicUrl))
        {
            throw new InvalidOperationException(
                "Static URL Provider selected but no Public URL is configured in settings.");
        }

        LastStartTime = DateTime.Now;

        // Simply return the configured static URL.
        // We assume the user has already handled port forwarding or DNS.
        return Task.FromResult(cfg.PublicUrl);
    }

    public Task StopTunnelAsync()
    {
        // No-op: Nothing to kill
        LastStartTime = null;
        return Task.CompletedTask;
    }
}