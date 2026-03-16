using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;

using Common.Interfaces;
using Common.Models.Database;

using Microsoft.EntityFrameworkCore;

using ProcessorApplication.Configuration;
using ProcessorApplication.Database;

namespace ProcessorApplication.Services.Settings;

public class SettingService : ISettingService
{
    private readonly ILogger<SettingService> _logger;
    private readonly AppDbContext _db;
    private readonly IConfiguration _config;
    private readonly IHostEnvironment _env;
    private readonly List<IDbConfigurationProvider> _dbProviders;
    private bool _autoUpdateSettings = true;

    private static readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNameCaseInsensitive = true
    };

    public SettingService(
        ILogger<SettingService> logger,
        AppDbContext db, 
        IConfiguration config, 
        IHostEnvironment env)
    {
        _logger = logger;
        _db = db;
        _config = config;
        _env = env;
        
        if (config is IConfigurationRoot configRoot)
        {
            _dbProviders = configRoot.Providers
                .OfType<IDbConfigurationProvider>()
                .ToList();

            _logger.LogDebug("SettingService initialized with {Count} database configuration providers.", _dbProviders.Count);
        }
        else
        {
            _dbProviders = new List<IDbConfigurationProvider>();
        }
    }

    public async Task SeedDefaultsIfEmptyAsync(CancellationToken stoppingToken)
    {
        var area = MainModule.MainId;
        // Use the application's root directory for the main appsettings file
        var mainSettingsFile = Path.Combine(_env.ContentRootPath, $"appsettings.{area}.json");

        if (File.Exists(mainSettingsFile) && !_db.Settings.Any(s => s.Area == area))
        {
            await LoadSettings(area, mainSettingsFile, stoppingToken);
        }

        // 2. Discover and Handle other Modules (Located in the Modules subdirectory)
        var modulesPath = Path.Combine(_env.ContentRootPath, "Modules");
        if (Directory.Exists(modulesPath))
        {
            foreach (var dir in Directory.GetDirectories(modulesPath))
            {
                area = Path.GetFileName(dir);
                var settingsFile = Path.Combine(dir, $"appsettings.{area}.json");

                if (!File.Exists(settingsFile) || _db.Settings.Any(s => s.Area == area)) continue;
                if (await _db.Settings.AnyAsync(s => s.Area == area, stoppingToken))
                    continue; // Skip if defaults already exist

                await LoadSettings(area, settingsFile, stoppingToken);
            }
        }

        if (_db.ChangeTracker.HasChanges())
        {
            await _db.SaveChangesAsync(stoppingToken);
            ForceUpdateOptionsMonitor(); // Trigger reload after seeding
        }
    }

    
    private async Task LoadSettings(string area, string filePath, CancellationToken stoppingToken)
    {
        _logger.LogInformation("Seeding defaults for new module: {Area}", area);


        var options = new JsonSerializerOptions
        {
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

        var json = await File.ReadAllTextAsync(filePath, stoppingToken);

        // We deserialize the entire JSON file into a root JsonElement
        var rootElement = JsonSerializer.Deserialize<JsonElement>(json, options);

        // Dictionary to hold all the flattened Key:Value pairs
        var flatSettings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Iterate through the top-level properties (e.g., "EmailSettings", "SecuritySettings")
        if (rootElement.ValueKind != JsonValueKind.Object)
        {
            _logger.LogError("Settings file is not a valid JSON object: {FilePath}", filePath);
            return;
        }

        foreach (var section in rootElement.EnumerateObject())
        {
            // Skip excluded sections
            if (section.Name.Contains("ConnectionStrings", StringComparison.OrdinalIgnoreCase)) continue;
            if (section.Name.Contains("EmailProviderGuessRules", StringComparison.OrdinalIgnoreCase)) continue;

            // Start the recursive flattening process
            var prefix = section.Name;
            FlattenJsonElement(section.Value, prefix, flatSettings);
        }

        _logger.LogInformation("Saving {Count} settings for module {Area}", flatSettings.Count, area);

        foreach (var kvp in flatSettings)
        {
            // Check for existing setting to prevent overwriting user changes
            var dbSetting = _db.Settings.FirstOrDefault(e => e.Key == kvp.Key && e.Area == area);

            if (dbSetting == null)
            {
                _db.Settings.Add(new Setting
                {
                    Area = area,
                    Key = kvp.Key,
                    Value = kvp.Value
                });
            }
            // NOTE: If you need to update existing settings, the logic goes here.
        }

        await _db.SaveChangesAsync(stoppingToken);

        /*
        var options = new JsonSerializerOptions
        {
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

        var json = await File.ReadAllTextAsync(filePath, stoppingToken);
        var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json, options);

        foreach (var section in dict!)
        {
            if (section.Key.Contains("ConnectionStrings", StringComparison.OrdinalIgnoreCase)) continue;
            if (section.Key.Contains("EmailProviderGuessRules", StringComparison.CurrentCulture)) continue;

            var prefix = $"{section.Key}:"; // e.g. "SecuritySettings:"

            foreach (var prop in section.Value.EnumerateObject())
            {
                var key = prefix + prop.Name; // e.g. "SecuritySettings:HashStampGenerationPeriod"
                var value = prop.Value.ToString();

                var dbSetting = _db.Settings.FirstOrDefault(e => e.Key == key && e.Area == area);

                if (dbSetting == null)
                {
                    _db.Settings.Add(new Setting
                    {
                        Area = area,
                        Key = key,
                        Value = value
                    });
                }
            }
        }*/
    }

    private void FlattenJsonElement(JsonElement element, string path, Dictionary<string, string> results)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    var newPath = path + ":" + property.Name;
                    FlattenJsonElement(property.Value, newPath, results);
                }
                break;

            case JsonValueKind.Array:
                for (int i = 0; i < element.GetArrayLength(); i++)
                {
                    var newPath = path + $"[{i}]";
                    FlattenJsonElement(element[i], newPath, results);
                }
                break;

            case JsonValueKind.String:
            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
                results.Add(path, element.ToString());
                break;

            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                results.Add(path, null);
                break;
        }
    }
    
    public async Task SetAsync<T>(string area, string key, T value)
    {
        string? valueToSave = SerializeValue(value);

        var entry = await _db.Settings.FirstOrDefaultAsync(s => s.Area == area && s.Key == key);
        if (entry == null)
        {
            entry = new Setting { Area = area, Key = key };
            _db.Settings.Add(entry);
        }

        entry.Value = valueToSave;
        await _db.SaveChangesAsync();

        if (_autoUpdateSettings) ForceUpdateOptionsMonitor();
    }

    private string? SerializeValue<T>(T value)
    {
        if (value == null) return null;
        if (value is string s) return s;

        var type = typeof(T);
        var underlyingType = Nullable.GetUnderlyingType(type) ?? type;

        if (underlyingType.IsPrimitive || underlyingType.IsEnum || underlyingType == typeof(decimal) || 
            underlyingType == typeof(double) || underlyingType == typeof(DateTime) || underlyingType == typeof(Guid))
        {
            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        return JsonSerializer.Serialize(value, _jsonOptions);
    }

    public async Task<T> GetAsync<T>(string area, string key, T defaultValue) where T : class, new()
    {
        var dbEntry = await _db.Settings.FirstOrDefaultAsync(s => s.Area == area && s.Key == key);
        if (dbEntry?.Value != null)
        {
            if (typeof(T) == typeof(string)) return (dbEntry.Value as T)!;
            return JsonSerializer.Deserialize<T>(dbEntry.Value, _jsonOptions) ?? defaultValue;
        }

        return _config.GetSection($"{area}:{key}").Get<T>() ?? defaultValue;
    }

    public void SetAutoUpdate(bool autoupdate)
    {
        _autoUpdateSettings = autoupdate;
    }

    public void ForceUpdateOptionsMonitor()
    {
        if (!_dbProviders.Any())
        {
            _logger.LogWarning("No IDbConfigurationProvider instances found to reload.");
            return;
        }

        _logger.LogInformation("Triggering reload on {Count} configuration providers...", _dbProviders.Count);

        // Iterate backwards: Last provider added is the highest priority.
        // Refreshing them ensures that when IOptionsMonitor re-binds, 
        // it sees the latest DB state from every possible source.
        for (int i = _dbProviders.Count - 1; i >= 0; i--)
        {
            try
            {
                _dbProviders[i].TriggerReload();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error reloading configuration provider at index {Index}", i);
            }
        }
    }

}