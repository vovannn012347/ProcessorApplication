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
        //var sectionName = typeof(T).Name; // e.g., "SecuritySettings"

        //// We look for the section prefixed by the module ID
        //// Because Step 1 (JSON Loader) and Step 2 (DB Provider) both prefix with "Area:"
        ////var combinedKey = $"{moduleId}:{sectionName}";
        //var section = config.GetSection(sectionName);

        //// Register Named Options
        //services.Configure<T>(moduleId, section);

        //// Register Singleton for direct injection
        ////services.AddSingleton(sp =>
        ////    sp.GetRequiredService<IOptionsMonitor<T>>().Get(moduleId));

        var sectionName = typeof(T).Name;
        var section = config.GetSection(sectionName);
        services.Configure<T>(moduleId, section);
        services.Configure<T>(section);
        return services;
    }

    public static IConfigurationBuilder AddModuleJsonFiles(
        this IConfigurationBuilder builder,
        string contentRootPath,
        string environment)
    {
        // Files come back in load order: per area the base file first, then its environment override,
        // so later files override earlier ones for the same keys.
        foreach (var (areaName, filePath) in GetModuleJsonFiles(contentRootPath, environment))
        {
            LoadAndPrefixJson(builder, areaName, filePath);
        }

        return builder;
    }

    /// <summary>
    /// The module settings files that exist for this environment, in load order (per area: base file, then the
    /// environment override). Shared by the configuration loader and the settings re-ingest service, so both
    /// always read exactly the same files.
    /// Main: appsettings.main.json / appsettings.main.{env}.json in the content root.
    /// Modules: Modules/{Name}Module/appsettings.{name}.json / appsettings.{name}.{env}.json, area = folder name without "Module".
    /// </summary>
    public static IEnumerable<(string Area, string FilePath)> GetModuleJsonFiles(string contentRootPath, string environment)
    {
        // --- 1. Handle Main Module ---
        foreach (var file in GetAreaFiles(contentRootPath, "appsettings.main", environment))
            yield return ("Main", file);

        // --- 2. Handle Sub-Modules ---
        string modulesPath = Path.Combine(contentRootPath, "Modules");

        if (Directory.Exists(modulesPath))
        {
            foreach (var dir in Directory.GetDirectories(modulesPath))
            {
                var dirName = Path.GetFileName(dir);

                var areaName = dirName.EndsWith("Module", StringComparison.OrdinalIgnoreCase)
                    ? dirName.Substring(0, dirName.Length - 6)
                    : dirName;

                var baseFileName = $"appsettings.{areaName.ToLower()}";

                foreach (var file in GetAreaFiles(dir, baseFileName, environment))
                    yield return (areaName, file);
            }
        }
    }

    private static IEnumerable<string> GetAreaFiles(string basePath, string baseFileName, string environment)
    {
        // 1. Base configuration (lowest priority, loaded FIRST). Example: appsettings.main.json
        var defaultFile = FindFileIgnoreCase(basePath, $"{baseFileName}.json");
        if (defaultFile != null)
            yield return defaultFile;

        // 2. Environment override (higher priority, loaded SECOND). Example: appsettings.main.Development.json
        if (!string.IsNullOrEmpty(environment))
        {
            var envFile = FindFileIgnoreCase(basePath, $"{baseFileName}.{environment}.json");
            if (envFile != null)
                yield return envFile;
        }
    }

    /// <summary>
    /// The file names are built in lower case ("appsettings.p2p.json") while the files on disk are
    /// "appsettings.P2P.json". Windows doesn't care; on Linux (headless nodes) an exact-case lookup
    /// would silently skip every module settings file, so match the name case-insensitively.
    /// </summary>
    private static string? FindFileIgnoreCase(string directory, string fileName)
    {
        var exact = Path.Combine(directory, fileName);
        if (File.Exists(exact))
            return exact;

        if (!Directory.Exists(directory))
            return null;

        return Directory.EnumerateFiles(directory, "*.json")
            .FirstOrDefault(f => string.Equals(Path.GetFileName(f), fileName, StringComparison.OrdinalIgnoreCase));
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
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
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
