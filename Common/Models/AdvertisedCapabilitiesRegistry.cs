using Microsoft.EntityFrameworkCore;

namespace Common.Models;

/// <summary>
/// Immutable catalog container holding all static capabilities advertised across discovered application modules.
/// Injected directly into controllers and services requiring available capability/role lookups.
/// </summary>
public class AdvertisedCapabilitiesRegistry
{
    private readonly HashSet<string> _capabilities;

    public IReadOnlyCollection<string> All => _capabilities;

    public AdvertisedCapabilitiesRegistry(IEnumerable<string> capabilities)
    {
        _capabilities = new HashSet<string>(capabilities ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
    }

    public bool Contains(string capability) => !string.IsNullOrWhiteSpace(capability) && _capabilities.Contains(capability);

    public override string ToString() => string.Join(", ", _capabilities);
}