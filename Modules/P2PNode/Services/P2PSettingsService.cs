using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Options;

using ProcessorApplication.Models;

namespace ProcessorApplication.Services;
public class P2PSettingsService
{
    private readonly IOptionsMonitor<P2PSettings> _settings;
    public event EventHandler<P2PSettings>? SettingsChanged;

    public P2PSettingsService(IOptionsMonitor<P2PSettings> settings)
    {
        _settings = settings;
        _settings.OnChange(updatedSettings =>
        {
            SettingsChanged?.Invoke(this, updatedSettings);
            Console.WriteLine($"P2PSettings updated: Port={updatedSettings.Port}, Interval={updatedSettings.GossipIntervalSeconds}");
        });
    }

    public P2PSettings CurrentSettings => _settings.CurrentValue;
}