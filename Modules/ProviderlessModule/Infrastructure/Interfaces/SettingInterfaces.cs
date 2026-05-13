using ProviderlessModule.Configuration;

namespace ProviderlessModule.Infrastructure.Interfaces;

/// <summary>
/// Marker interface for any provider settings (Cloudflare, GitHub, etc.)
/// </summary>
public interface IProviderSettings {
    string ProviderKey { get; }
    string ProviderName { get; }
    string SettingsViewPath { get; } // for extensibility
}
public interface IRegistrySettings : IProviderSettings {
}
public interface ITunnelSettings : IProviderSettings { }