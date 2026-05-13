
using System.Collections.Concurrent;
using System.Reflection;

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Rendering;

using ProviderlessModule.Configuration;
using ProviderlessModule.Configuration.Registry;
using ProviderlessModule.Configuration.Tunnel;
using ProviderlessModule.Infrastructure.Interfaces;

namespace ProviderlessModule.Models;

public class ProviderlessSettingsViewModel
{
    public PortalAccessSettings General { get; set; }

    /// <summary>
    /// Key: Tunnel ID (e.g., "cloudflare", "ngrok")
    /// Value: IProviderSettings implementation
    /// </summary>

    [ModelBinder(BinderType = typeof(ProviderlessSettingsBinder))]
    public Dictionary<string, IProviderSettings> TunnelSettings { get; set; } = new();
    public List<SelectListItem> TunnelOptions { get; set; } = new();

    /// <summary>
    /// Key: Registry ID (e.g., "github", "googledocs")
    /// Value: IProviderSettings implementation
    /// </summary>

    [ModelBinder(BinderType = typeof(ProviderlessSettingsBinder))]
    public Dictionary<string, IProviderSettings> RegistrySettings { get; set; } = new();
    public List<SelectListItem> RegistryOptions { get; set; } = new();
}

public class ProviderlessSettingsBinder : IModelBinder
{
    private static readonly ConcurrentDictionary<string, Type> _typeCache = new();

    public async Task BindModelAsync(ModelBindingContext bindingContext)
    {
        var resultDictionary = new Dictionary<string, IProviderSettings>();
        var prefix = bindingContext.ModelName; // e.g., "TunnelSettings"

        // 1. Identify all unique provider keys in the form data (e.g., "cloudflare", "ngrok")
        var formKeys = bindingContext.HttpContext.Request.Form.Keys
            .Where(k => k.StartsWith(prefix + "["))
            .Select(k => k.Substring(prefix.Length + 1, k.IndexOf(']') - (prefix.Length + 1)))
            .Distinct();

        foreach (var providerKey in formKeys)
        {
            // 2. Resolve the concrete type (using your existing cache/scan logic)
            if (!_typeCache.TryGetValue(providerKey.ToLower(), out var concreteType))
            {
                concreteType = AppDomain.CurrentDomain.GetAssemblies()
                    .SelectMany(a => { try { return a.GetTypes(); } catch { return Type.EmptyTypes; } })
                    .FirstOrDefault(t => typeof(IProviderSettings).IsAssignableFrom(t)
                                    && !t.IsInterface && !t.IsAbstract
                                    && (t.Namespace?.Contains("Providerless") ?? false)
                                    && ((IProviderSettings)Activator.CreateInstance(t)).ProviderKey.Equals(providerKey, StringComparison.OrdinalIgnoreCase));

                if (concreteType != null) _typeCache[providerKey.ToLower()] = concreteType;
            }

            if (concreteType == null) continue;

            // 3. Create and bind the concrete instance
            var instance = Activator.CreateInstance(concreteType);
            var instancePrefix = $"{prefix}[{providerKey}]";

            foreach (var prop in concreteType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!prop.CanWrite) continue;

                var fieldName = $"{instancePrefix}.{prop.Name}";
                var val = bindingContext.ValueProvider.GetValue(fieldName).FirstValue;

                if (val != null)
                {
                    try { prop.SetValue(instance, Convert.ChangeType(val, prop.PropertyType)); }
                    catch { /* Handle parse failure */ }
                }
            }

            resultDictionary[providerKey] = (IProviderSettings)instance;
        }

        bindingContext.Result = ModelBindingResult.Success(resultDictionary);
        await Task.CompletedTask;
    }
}