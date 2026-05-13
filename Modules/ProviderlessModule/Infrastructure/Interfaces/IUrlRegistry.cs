using ProviderlessModule.Configuration;

namespace ProviderlessModule.Infrastructure.Interfaces;

/// <summary>
/// Defines a provider-specific implementation for publishing encrypted tunnel data.
/// </summary>
public interface IUrlRegistry
{
    /// <summary>
    /// The type of provider this implementation handles.
    /// </summary>
    string Provider { get; }
    bool IsActive { get; protected set; }

    /// <summary>
    /// Asynchronously pushes encrypted data to the external registry.
    /// </summary>
    Task RegisterAccessAsync(string tunnelUrl, CancellationToken ct);

    /// <summary>
    /// Returns the absolute URL that the QR code should point to 
    /// for this specific registry entry.
    /// </summary>
    string GetQrDiscoveryUrl();
}
