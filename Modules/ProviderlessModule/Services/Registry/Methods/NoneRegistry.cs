using ProviderlessModule.Configuration;
using ProviderlessModule.Infrastructure.Interfaces;

namespace ProviderlessModule.Services.Registry.Methods;
/// <summary>
/// A "Direct-Link" provider that skips external registry syncing.
/// The QR code points directly to the current live tunnel URL.
/// </summary>
public class NoneRegistry : IUrlRegistry
{
    private readonly PortalState _portalState;

    public NoneRegistry(PortalState portalState)
    {
        _portalState = portalState;
    }

    public string Provider => RegistryProviderType.None;

    bool IUrlRegistry.IsActive { get => false; set { } }

    /// <summary>
    /// Does nothing. No external registration is required for direct links.
    /// </summary>
    public Task RegisterAccessAsync(string tunnelUrl, CancellationToken ct)
        => Task.CompletedTask;

    /// <summary>
    /// Ignores the encrypted key and returns the raw, unencrypted tunnel URL 
    /// currently held in the PortalState singleton.
    /// </summary>
    public string GetQrDiscoveryUrl()
    {
        // just hand back the direct link as-is.
        return _portalState.CurrentTunnelUrl ?? string.Empty;
    }
}