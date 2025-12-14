using System;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Options;

namespace ProcessorApplication.Utils;

public static class ModuleConfigurationExtensions
{
    public static IServiceCollection AddModuleSettings<T>(
            this IServiceCollection services,
            IConfiguration config,
            string moduleId) where T : class
    {
        var sectionName = typeof(T).Name; // e.g., "SecuritySettings"

        // We look for the section prefixed by the module ID
        // Because Step 1 (JSON Loader) and Step 2 (DB Provider) both prefix with "Area:"
        //var combinedKey = $"{moduleId}:{sectionName}";
        var section = config.GetSection(sectionName);

        // Register Named Options
        services.Configure<T>(moduleId, section);

        // Register Singleton for direct injection
        //services.AddSingleton(sp =>
        //    sp.GetRequiredService<IOptionsMonitor<T>>().Get(moduleId));

        return services;
    }

    public static IConfigurationBuilder AddModuleJsonFiles(this IConfigurationBuilder builder, string contentRootPath)
    {
        // --- 1. Handle Main Module (Root Directory) ---
        // We hardcode "main" here or you can pass it as a constant
        string mainArea = "Main";
        string mainFile = Path.Combine(contentRootPath, $"appsettings.main.json");

        if (File.Exists(mainFile))
        {
            LoadAndPrefixJson(builder, mainArea, mainFile);
        }

        // --- 2. Handle Sub-Modules (Modules Directory) ---
        string modulesPath = Path.Combine(contentRootPath, "Modules");

        if (Directory.Exists(modulesPath))
        {
            foreach (var dir in Directory.GetDirectories(modulesPath))
            {
                var areaName = Path.GetFileName(dir); // e.g., "P2P"
                var settingsFile = Path.Combine(dir, $"appsettings.{areaName}.json");

                if (File.Exists(settingsFile))
                {
                    LoadAndPrefixJson(builder, areaName, settingsFile);
                }
            }
        }

        return builder;
    }

    private static void LoadAndPrefixJson(IConfigurationBuilder builder, string areaName, string filePath)
    {
        // 1. Read content
        var jsonContent = File.ReadAllText(filePath);

        // 2. Flatten keys (e.g. "SecuritySettings:HashPeriod")
        var flatData = Flatten(jsonContent);

        // 3. Prefix keys with Area (e.g. "Main:SecuritySettings:HashPeriod")
        // This ALIGNS with the DbConfigurationProvider's format.
        var prefixedData = flatData.ToDictionary(
            kvp => $"{areaName}:{kvp.Key}",
            kvp => kvp.Value
        );

        // 4. Inject into configuration
        builder.AddInMemoryCollection(prefixedData);
    }

    public static Dictionary<string, string?> Flatten(string json)
    {
        var dict = new Dictionary<string, string?>();
        var bytes = Encoding.UTF8.GetBytes(json);
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions
        {
            CommentHandling = JsonCommentHandling.Skip
        });
        using var document = JsonDocument.ParseValue(ref reader);
        VisitElement(document.RootElement, string.Empty, dict);
        return dict;
    }

    private static void VisitElement(JsonElement element, string prefix, Dictionary<string, string?> dict)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in element.EnumerateObject())
                {
                    var nextKey = string.IsNullOrEmpty(prefix) ? prop.Name : $"{prefix}:{prop.Name}";
                    VisitElement(prop.Value, nextKey, dict);
                }
                break;
            case JsonValueKind.Array:
                int index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    var nextKey = $"{prefix}:{index}";
                    VisitElement(item, nextKey, dict);
                    index++;
                }
                break;
            default:
                dict[prefix] = element.ToString();
                break;
        }
    }
}
