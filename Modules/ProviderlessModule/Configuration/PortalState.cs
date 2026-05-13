using System.ComponentModel.DataAnnotations;

namespace ProviderlessModule.Configuration;

/// <summary>
/// Represents the live, volatile state of the connectivity portal.
/// This is a runtime singleton and is not persisted to static configuration.
/// </summary>
public class PortalState
{

    /// <summary>
    /// The currently active public tunnel URL
    /// Null or empty if the tunnel is offline.
    /// </summary>
    [Display(Name = "Live Tunnel URL")]
    public string? CurrentTunnelUrl { get; set; }
    public string? CurrentQrUrl { get; set; }
    public bool IsActive { get; set; }
    public DateTime TunnelStartedAt { get; set; }
    public string LastError { get; set; }
    public bool RestartRequested { get; set; }
}
