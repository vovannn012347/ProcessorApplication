using System.ComponentModel.DataAnnotations;

using Microsoft.Extensions.Options;

using ProviderlessModule.Configuration;
using ProviderlessModule.Infrastructure;

namespace ProviderlessModule.Services.Registry.Methods;

//public class RestRegistry : IUrlRegistry
//{
//    private readonly IOptionsMonitor<PortalAccessSettings> _settings;

//    public RestRegistry(
//        IOptionsMonitor<PortalAccessSettings> settings)
//    {
//        _settings = settings;
//    }

//    public RegistryProviderType Provider => RegistryProviderType.Rest;

//    public string GetQrDiscoveryUrl(string encryptedKey)
//    {
//        throw new NotImplementedException();
//    }

//    public Task RegisterAccessAsync(string encryptedKey, string tunnelUrl, CancellationToken ct)
//    {
//        throw new NotImplementedException();
//    }

//    public Task<string?> ResolveAccessAsync(string encryptedKey, CancellationToken ct)
//    {
//        throw new NotImplementedException();
//    }

//    public Task<bool> UpdateRegistryAsync(string encryptedData, CancellationToken ct = default)
//    {
//        throw new NotImplementedException();
//    }
//}