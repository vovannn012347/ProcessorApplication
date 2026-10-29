using System.ComponentModel.DataAnnotations;

using ProviderlessModule.Infrastructure.Interfaces;

namespace ProviderlessModule.Configuration.Tunnel;

/// <summary>
/// Cloudflare tunnel provisioned by the provisioning Worker (Tools/cloudflare-provisioner).
/// The Worker holds the Cloudflare API token; this node has no Cloudflare credentials.
/// It signs its requests with its own key (Module_Data/Portal/keys) and gets back the tunnel token
/// for its own subdomain only.
/// </summary>
public class CloudflareWorkerSettings : ITunnelSettings
{
    public string ProviderKey => TunnelProviderType.CloudflareWorker;

    public string ProviderName => "CloudflareWorkerSettings_key";
    public string SettingsViewPath => "~/Views/Providerless/Settings/_Settings_Tun_cloudflareworker.cshtml";

    /// <summary>
    /// Base URL of the provisioning Worker, e.g. https://processor-provisioner.example.workers.dev.
    /// Only the scheme and host are used. Must be https (http is accepted for localhost only).
    /// </summary>
    [Display(Name = "Provisioning Service URL")]
    public string ServiceUrl { get; set; } = string.Empty;

    /// <summary>
    /// Pool join key, if the Worker has JOIN_KEY set.
    /// </summary>
    [Display(Name = "Join Key")]
    public string JoinKey { get; set; } = string.Empty;

    /// <summary>
    /// Replace the tunnel (and with it the tunnel token) when it is older than this many days.
    /// Checked when the tunnel starts. 0 = never.
    /// </summary>
    [Range(0, 3650)]
    [Display(Name = "Tunnel Rotation (Days)")]
    public int TunnelRotationDays { get; set; } = 0;

    /// <summary>
    /// Tunnel token last returned by the Worker. Written automatically; used to start the tunnel
    /// when the Worker is unreachable.
    /// </summary>
    [Display(Name = "Tunnel Token (automatic)")]
    public string TunnelToken { get; set; } = string.Empty;

    /// <summary>
    /// Additional arguments to pass to the cloudflared process.
    /// </summary>
    [Display(Name = "Extra Arguments")]
    public string ExtraArgs { get; set; } = "";

    /// <summary>
    /// Maximum time to wait for cloudflared to connect.
    /// </summary>
    [Range(10, 120)]
    [Display(Name = "Connection Timeout (Seconds)")]
    public int WaitTimeoutSeconds { get; set; } = 45;

    // Platform-specific cloudflared binary (same binary as the token-based Cloudflare tunnel)
    public CloudflareSettings.BinaryPlatformConfig Windows { get; set; } = new();
    public CloudflareSettings.BinaryPlatformConfig Linux { get; set; } = new();
}
