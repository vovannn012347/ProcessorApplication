using System.Security.Cryptography;
using System.Text;

using Common.Interfaces;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using ProviderlessModule.Code;
using ProviderlessModule.Configuration;

namespace ProviderlessModule.Services;

public interface ILocalDataProvider
{
    string GetEncryptedMachineHash();
    string GetRegistryAlias();
    string GetSharedSecret();
}

public class LocalDataProvider : ILocalDataProvider
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptionsMonitor<PortalAccessSettings> _settings;
    private readonly object _lock = new();

    public LocalDataProvider(
        IOptionsMonitor<PortalAccessSettings> monitor,
        IServiceScopeFactory scopeFactory)
    {
        _settings = monitor;
        _scopeFactory = scopeFactory;
    }

    public string GetEncryptedMachineHash()
    {
        if (string.IsNullOrWhiteSpace(_settings.CurrentValue.ComputerId))
        {
            lock (_lock)
            {
                if (string.IsNullOrWhiteSpace(_settings.CurrentValue.ComputerId))
                {
                    _settings.CurrentValue.ComputerId = GenerateHardwareHash();

                    Task.Run(async () =>
                    {
                        using var scope = _scopeFactory.CreateScope();
                        var settingsService = scope.ServiceProvider.GetRequiredService<ISettingService>();
                        string key = ConfigurationPathHelper.GetPath((PortalAccessSettings s) => s.ComputerId);
                        await settingsService.SetAsync(ProviderlessModule.MODULE_ID, key, _settings.CurrentValue.ComputerId);
                    });
                }
            }
        }
        return _settings.CurrentValue.ComputerId;
    }

    public string GetSharedSecret()
    {
        if (string.IsNullOrWhiteSpace(_settings.CurrentValue.SharedSecret))
        {
            lock (_lock)
            {
                if (string.IsNullOrWhiteSpace(_settings.CurrentValue.SharedSecret))
                {
                    _settings.CurrentValue.SharedSecret = GenerateRandomSecret(12);

                    Task.Run(async () =>
                    {
                        using var scope = _scopeFactory.CreateScope();
                        var settingsService = scope.ServiceProvider.GetRequiredService<ISettingService>();
                        string key = ConfigurationPathHelper.GetPath((PortalAccessSettings s) => s.SharedSecret);
                        await settingsService.SetAsync(ProviderlessModule.MODULE_ID, key, _settings.CurrentValue.SharedSecret);
                    });
                }
            }
        }

        return _settings.CurrentValue.SharedSecret;
    }

    private string GenerateHardwareHash()
    {
        // Stable hardware string
        string raw = $"{Environment.MachineName}-{Environment.ProcessorCount}-{Environment.OSVersion}";
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));

        // Take 12 chars from the hex representation
        return Convert.ToHexString(hash).Substring(0, 12).ToLower();
    }
    public string GetRegistryAlias()
    {
        // Double-derive: Hash the real ID with a salt to hide it
        string realId = GetEncryptedMachineHash();
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(realId));
        return Convert.ToHexString(hash).Substring(0, 16).ToLower(); // 16-char obscured name
    }

    private string GenerateRandomSecret(int length)
    {
        const string chars = "abcdefghijklmnopqrstuvwxyz0123456789";
        return new string(Enumerable.Repeat(chars, length)
            .Select(s => s[RandomNumberGenerator.GetInt32(s.Length)]).ToArray());
    }
}