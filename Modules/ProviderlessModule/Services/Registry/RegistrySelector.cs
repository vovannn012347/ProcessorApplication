using System.ComponentModel.DataAnnotations;

using Microsoft.Extensions.Options;

using ProviderlessModule.Configuration;
using ProviderlessModule.Infrastructure.Interfaces;

namespace ProviderlessModule.Services.Registry;


public class RegistrySelector : IRegistrySelector
{
    private readonly IEnumerable<IUrlRegistry> _registries;
    private readonly IOptionsMonitor<PortalAccessSettings> _settings;

    public RegistrySelector(IEnumerable<IUrlRegistry> registries, 
        IOptionsMonitor<PortalAccessSettings> settings)
    {
        _registries = registries;
        _settings = settings;
    }

    public IUrlRegistry GetActiveRegistry()
    {
        var activeType = _settings.CurrentValue.ActiveRegistry.ToLower();

        // Return the matched provider, or fallback to None if not found
        return _registries.FirstOrDefault(x => x.Provider.ToLower() == activeType)
               ?? _registries.First(x => x.Provider == RegistryProviderType.None);
    }
}