using ProviderlessModule.Configuration;

namespace ProviderlessModule.Infrastructure.Interfaces;

/// <summary>
/// Defines the contract for ensuring necessary external binaries 
/// (Cloudflare, Ngrok) are present and executable on the host system.
/// </summary>
public interface IBinaryBootstrapper
{
    /// <summary>
    /// Checks for existence and, if necessary, downloads/prepares 
    /// the binary for the active tunnel provider.
    /// </summary>
    Task EnsureBinariesAsync(CancellationToken ct = default);
}