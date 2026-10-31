namespace ProviderlessModule.Models;

internal class QrStatusResult
{
    public bool TunnelActive { get; set; }
    public bool RegistyActive { get; set; }
    /// <summary>What the QR code encodes: PublicUrl when there is one, otherwise TunnelUrl.</summary>
    public string Url { get; set; }
    /// <summary>Direct tunnel address of this node, while the tunnel runs.</summary>
    public string? TunnelUrl { get; set; }
    /// <summary>Resolver link with this node's registry id and key (QR target once the registry is synced).</summary>
    public string? PublicUrl { get; set; }
    /// <summary>The address the registry currently holds for this node (decrypted).</summary>
    public string? RegisteredUrl { get; set; }
    /// <summary>This node's entry on the registry site (e.g. the GitHub file).</summary>
    public string? RegistryEntryUrl { get; set; }
    public bool RegistryEnabled { get; set; }
    public string StartTime { get; set; }
    public string ProviderName { get; set; }
    public string LastError { get; set; }
}