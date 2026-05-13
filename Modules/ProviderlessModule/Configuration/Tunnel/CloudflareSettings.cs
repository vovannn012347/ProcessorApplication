using System.ComponentModel.DataAnnotations;

using ProviderlessModule.Infrastructure.Interfaces;

namespace ProviderlessModule.Configuration.Tunnel;

public class CloudflareSettings : ITunnelSettings
{
    public string ProviderKey => "cloudflare";

    public string ProviderName => "CloudflareSettings_key";
    public string SettingsViewPath => "~/Views/Providerless/Settings/_Settings_Tun_cloudflare.cshtml";

    /// <summary>
    /// If provided, the agent will attempt to connect to a specific named tunnel.
    /// If null or cannot conenct to - a 'Quick Tunnel' (random URL) will be created.
    /// </summary>
    [Display(Name = "Tunnel Token (Optional)")]
    public string TunnelToken { get; set; }

    /// <summary>
    /// If tunnel is not provided - agent will neeed provisioning done
    /// accoutn id, zone id and provision token should be set for this
    /// </summary>
    [Display(Name = "Account ID")]
    public string AccountId { get; set; }
    [Display(Name = "Zone ID")]
    public string ZoneId { get; set; }

    [Display(Name = "Provision Token")]
    public string ProvisionToken { get; set; }
    /// <summary>
    /// If tunnel token is provided, the agent will assume user knows what it is doing.
    /// If null or cannot conenct to - a 'Quick Tunnel' (random URL) will be created.
    /// </summary>
    [Display(Name = "Custom public url (Optional)")]
    public string? CustomPublicUrl { get; set; }

    /// <summary>
    /// Additional arguments to pass to the cloudflared process.
    /// Example: "--protocol http2"
    /// </summary>
    [Display(Name = "Extra Arguments")]
    public string ExtraArgs { get; set; } = "";

    /// <summary>
    /// Maximum time to wait for the tunnel URL to appear in the logs.
    /// </summary>
    [Range(10, 120)]
    [Display(Name = "Connection Timeout (Seconds)")] 
    public int WaitTimeoutSeconds { get; set; } = 30;

    // Platform-specific groups
    public BinaryPlatformConfig Windows { get; set; } = new();
    public BinaryPlatformConfig Linux { get; set; } = new();

    public class BinaryPlatformConfig
    {
        public string BinaryPath { get; set; }
        public string DownloadUrl { get; set; }
    }
}
