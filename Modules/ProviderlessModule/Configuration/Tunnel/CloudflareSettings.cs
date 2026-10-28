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
    public string TunnelToken { get; set; } = string.Empty;

    /// <summary>
    /// If tunnel is not provided - agent will neeed provisioning done
    /// accoutn id, zone id and provision token should be set for this
    /// </summary>
    [Display(Name = "Account ID")]
    public string AccountId { get; set; } = string.Empty;
    [Display(Name = "Zone ID")]
    public string ZoneId { get; set; } = string.Empty;

    [Display(Name = "Provision Token")]
    public string ProvisionToken { get; set; } = string.Empty;

    /// <summary>
    /// How the tunnel and its DNS record get created when no usable TunnelToken is stored.
    /// "token": this node calls the Cloudflare API itself with ProvisionToken, AccountId and ZoneId.
    /// "service": this node asks the provisioning Worker (ProvisioningServiceUrl); no Cloudflare credentials on the node.
    /// </summary>
    [Display(Name = "Provisioning Mode")]
    public string ProvisioningMode { get; set; } = CloudflareProvisioningMode.Token;

    /// <summary>
    /// Base URL of the provisioning Worker, e.g. https://processor-provisioner.example.workers.dev.
    /// Only the scheme and host are used. Must be https (http is accepted for localhost only).
    /// </summary>
    [Display(Name = "Provisioning Service URL")]
    public string ProvisioningServiceUrl { get; set; } = string.Empty;

    /// <summary>
    /// Optional pool join key, if the Worker has JOIN_KEY set.
    /// </summary>
    [Display(Name = "Provisioning Join Key")]
    public string ProvisioningJoinKey { get; set; } = string.Empty;

    /// <summary>
    /// Service mode: replace the tunnel (and with it the tunnel token) when it is older than this many days.
    /// Checked when the tunnel starts. 0 = never.
    /// </summary>
    [Range(0, 3650)]
    [Display(Name = "Tunnel Rotation (Days)")]
    public int TunnelRotationDays { get; set; } = 0;
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

public static class CloudflareProvisioningMode
{
    public const string Token = "token";
    public const string Service = "service";

    public static bool IsService(string? mode) =>
        string.Equals(mode?.Trim(), Service, StringComparison.OrdinalIgnoreCase);
}
