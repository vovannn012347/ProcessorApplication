using System.ComponentModel.DataAnnotations;

using Microsoft.Extensions.Options;

using ProviderlessModule.Configuration;
using ProviderlessModule.Infrastructure.Interfaces;

namespace ProviderlessModule.Services.Tunnel;

public class TunnelSelector : ITunnelSelector
{
    private readonly IEnumerable<ITunnelProvider> _providers;
    private readonly IOptionsMonitor<PortalAccessSettings> _settings;

    public TunnelSelector(
        IEnumerable<ITunnelProvider> providers,
        IOptionsMonitor<PortalAccessSettings> settings)
    {
        _providers = providers;
        _settings = settings;
    }

    public ITunnelProvider GetActiveProvider()
    {
        var preferred = _settings.CurrentValue.SelectedTunnel.ToLower();
        return _providers.FirstOrDefault(p => p.Provider.ToLower() == preferred)
               ?? throw new NotSupportedException($"Tunnel provider {preferred} is not registered.");
    }
}