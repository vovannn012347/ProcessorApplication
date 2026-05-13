using System.ComponentModel.DataAnnotations;

using Microsoft.Extensions.Options;

namespace ProviderlessModule.Infrastructure.Interfaces;

/// <summary>
/// Service responsible for resolving the correct IUrlRegistry based on current security settings.
/// </summary>
public interface IRegistrySelector
{
    /// <summary>
    /// Resolves the active registry implementation.
    /// </summary>
    IUrlRegistry GetActiveRegistry();
}
