using Common.Code;
using Common.Interfaces;

using ProcessorApplication.Sqlite;
using ProcessorApplication.Sqlite.Models;

using Microsoft.EntityFrameworkCore;
using Common.Attributes;
using System.Reflection;
using MongoDB.Bson;
using System.Text.Json;

namespace ProcessorApplication.Services;


public class SettingService : ISettingService
{
    private readonly AppDbContext _db;
    private readonly IConfiguration _config;
    private readonly IHostEnvironment _env;

    //private readonly AppDbContext _context;
    //private readonly SqliteConfigurationProvider _configProvider;
    public SettingService(AppDbContext db, IConfiguration config, IHostEnvironment env)
    {
        _db = db;
        _config = config;
        _env = env;
    }

    public async Task SeedDefaultsIfEmptyAsync(CancellationToken stoppingToken)
    {
        //if (_db.Settings.Any()) return; // already seeded

        //var areas = Directory.GetDirectories(Path.Combine(_env.ContentRootPath, "Areas"));

        //foreach (var areaPath in areas)
        //{
        //    var areaName = Path.GetFileName(areaPath);
        //    var file = Path.Combine(areaPath, $"appsettings.{areaName}.json");
        //    if (!File.Exists(file)) continue;

        //    var json = await File.ReadAllTextAsync(file);
        //    var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);

        //    foreach (var section in dict!)
        //    {
        //        var prefix = $"{section.Key}:";               // "Order:"
        //        foreach (var prop in section.Value.EnumerateObject())
        //        {
        //            var key = prefix + prop.Name;             // "P2Psa:MaxItems"
        //            var value = prop.Value.GetRawText();      // JSON string

        //            _db.Settings.Add(new Setting
        //            {
        //                Area = areaName,
        //                Key = key,
        //                Value = value
        //            });
        //        }
        //    }
        //}

        await _db.SaveChangesAsync();

        // Discover all modules (even if not yet loaded)
        var modulesPath = Path.Combine(_env.ContentRootPath, "Modules");
        if (!Directory.Exists(modulesPath)) return;

        foreach (var dir in Directory.GetDirectories(modulesPath))
        {
            var area = Path.GetFileName(dir);
            var settingsFile = Path.Combine(dir, $"appsettings.{area}.json");

            if (!File.Exists(settingsFile)) continue;

            // Only seed if this area has NO settings in DB
            if (await _db.Settings.AnyAsync(s => s.Area == area, stoppingToken))
                continue;

            //_log.LogInformation("Seeding defaults for new module: {Area}", area);

            var json = await File.ReadAllTextAsync(settingsFile, stoppingToken);
            var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);

            foreach (var section in dict!)
            {
                var prefix = $"{section.Key}:"; // e.g. "Order:"
                foreach (var prop in section.Value.EnumerateObject())
                {
                    var key = prefix + prop.Name;
                    var value = prop.Value.GetRawText();

                    _db.Settings.Add(new Setting
                    {
                        Area = area,
                        Key = key,
                        Value = value
                    });
                }
            }
        }

        if (_db.ChangeTracker.HasChanges())
            await _db.SaveChangesAsync(stoppingToken);
    }

    //public async Task SeedMissingDefaultsAsync()
    //{
    //    if (_db.Settings.Any()) return; // already seeded

    //    var areas = Directory.GetDirectories(Path.Combine(_env.ContentRootPath, "Areas"));

    //    foreach (var areaPath in areas)
    //    {
    //        var areaName = Path.GetFileName(areaPath);
    //        var file = Path.Combine(areaPath, $"appsettings.{areaName}.json");
    //        if (!File.Exists(file)) continue;

    //        var json = await File.ReadAllTextAsync(file);
    //        var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);

    //        foreach (var section in dict!)
    //        {
    //            var prefix = $"{section.Key}:";               // "Order:"
    //            foreach (var prop in section.Value.EnumerateObject())
    //            {
    //                var key = prefix + prop.Name;             // "P2Psa:MaxItems"
    //                var value = prop.Value.GetRawText();      // JSON string

    //                _db.Settings.Add(new Setting
    //                {
    //                    Area = areaName,
    //                    Key = key,
    //                    Value = value
    //                });
    //            }
    //        }
    //    }

    //    foreach (var (area, key, value) in defaults)
    //    {
    //        if (!await _db.Settings.AnyAsync(s => s.Area == area && s.Key == key))
    //            _db.Settings.Add(new Setting { Area = area, Key = key, Value = value });
    //    }
    //    await _db.SaveChangesAsync();
    //}


    public async Task<T> GetAsync<T>(string area, string key, T defaultValue) where T : class, new()
    {
        var fullKey = $"{area}:{key}";
        var dbEntry = await _db.Settings
            .FirstOrDefaultAsync(s => s.Area == area && s.Key == fullKey);

        if (dbEntry != null && dbEntry.Value != null)
        {
            return JsonSerializer.Deserialize<T>(dbEntry.Value)!;
        }

        // ---- fallback to file default ----
        var fileSection = _config.GetSection($"{area}:{key}");
        if (fileSection.Exists())
        {
            var obj = fileSection.Get<T>();
            if (obj != null) return obj;
        }

        return defaultValue;
    }

    public async Task SetAsync<T>(string area, string key, T value)
    {
        var fullKey = $"{area}:{key}";
        var json = JsonSerializer.Serialize(value);

        var entry = await _db.Settings
            .FirstOrDefaultAsync(s => s.Area == area && s.Key == fullKey);

        if (entry == null)
        {
            entry = new Setting { Area = area, Key = fullKey };
            _db.Settings.Add(entry);
        }

        entry.Value = json;
        await _db.SaveChangesAsync();
    }

    //public SettingsService(AppDbContext context, SqliteConfigurationProvider configProvider)
    //{
    //    _context = context ?? throw new ArgumentNullException(nameof(context));
    //    _configProvider = configProvider ?? throw new ArgumentNullException(nameof(configProvider));
    //}

    //public async Task<T> GetSettingsAsync<T>() where T : class, ISettings
    //{
    //    var type = typeof(T);
    //    var prefix = type.GetCustomAttribute<SettingKeyAttribute>()?.Key ?? type.Name.Replace("Settings", "") + ":";

    //    var settings = await _context.Settings
    //        .AsNoTracking()
    //        .Where(s => s.Key.StartsWith(prefix))
    //        .ToDictionaryAsync(
    //            s => s.Key.Substring(prefix.Length),
    //            s => s.IsSensitive ? Util.Decrypt(s.Value ?? string.Empty) : s.Value ?? string.Empty);

    //    var instance = Activator.CreateInstance<T>();
    //    return (T)instance.FromDbSettings(settings);
    //}

    //public async Task SaveSettingsAsync<T>(T settings) where T : class, ISettings
    //{
    //    if (settings == null) throw new ArgumentNullException(nameof(settings));

    //    var dict = settings.GetSettingForDb();
    //    var type = typeof(T);
    //    var prefix = type.GetCustomAttribute<SettingKeyAttribute>()?.Key ?? type.Name.Replace("Settings", "") + ":";

    //    using var transaction = await _context.Database.BeginTransactionAsync();
    //    try
    //    {
    //        foreach (var kvp in dict)
    //        {
    //            if (string.IsNullOrEmpty(kvp.Key)) continue; // Skip invalid keys
    //            var key = $"{prefix}{kvp.Key}";
    //            var value = kvp.Value ?? string.Empty;
    //            var isSensitive = settings.IsSettingSensitive(kvp.Key);

    //            var setting = await _context.Settings.FirstOrDefaultAsync(s => s.Key == key);
    //            if (setting == null)
    //            {
    //                setting = new Setting
    //                {
    //                    Key = key,
    //                    Value = isSensitive ? Util.Encrypt(value) : value,
    //                    IsSensitive = isSensitive
    //                };
    //                _context.Settings.Add(setting);
    //            }
    //            else
    //            {
    //                setting.Value = isSensitive ? Util.Encrypt(value) : value;
    //                setting.IsSensitive = isSensitive;
    //            }
    //        }

    //        await _context.SaveChangesAsync();
    //        await transaction.CommitAsync();

    //        _configProvider.TriggerReload();
    //    }
    //    catch
    //    {
    //        await transaction.RollbackAsync();
    //        throw;
    //    }
    //}

    /*
     * this is for retrieving basic settings, no complex settings are retrievable
     * todo: add complex setting converter function
    **/
    //public async Task<T> GetSettingAsync<T>(
    //    string key, 
    //    T defaultValue, string prefix = "", 
    //    Func<string, T>? resultFunction = null)
    //{
    //    if (string.IsNullOrEmpty(key)) return defaultValue;

    //    if (!string.IsNullOrEmpty(prefix)) prefix = $"{prefix.Trim(':')}:";
    //    key = $"{prefix}{key}";

    //    var setting = await _context.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == key);
    //    if (setting == null) return defaultValue;

    //    var value = setting.IsSensitive ? Util.Decrypt(setting.Value ?? string.Empty) : setting.Value ?? string.Empty;

    //    try
    //    {
    //        if (typeof(T) == typeof(string)) return (T)(object)value;
    //        if (typeof(T) == typeof(int) && int.TryParse(value, out var intValue)) return (T)(object)intValue;
    //        if (typeof(T) == typeof(float) && float.TryParse(value, out var floatValue)) return (T)(object)floatValue;
    //        if (typeof(T) == typeof(double) && double.TryParse(value, out var doubleValue)) return (T)(object)doubleValue;
    //        if (typeof(T) == typeof(bool) && bool.TryParse(value, out var boolValue)) return (T)(object)boolValue;
    //        if (resultFunction != null)
    //        {
    //            return resultFunction(value);
    //        }
    //        else
    //        {
    //            try
    //            {
    //                return JsonSerializer.Deserialize<T>(value);
    //            }
    //            catch
    //            {

    //            }
    //        }

    //        return defaultValue;
    //    }
    //    catch
    //    {
    //        return defaultValue;
    //    }
    //}

    //public async Task<string> GetSettingAsync(string key, string defaultValue = "", string prefix = "")
    //{
    //    if (string.IsNullOrEmpty(key)) return defaultValue;

    //    if (!string.IsNullOrEmpty(prefix))
    //    {
    //        prefix = $"{prefix.Trim(':')}:";
    //        key = $"{prefix}{key}";
    //    }

    //    var setting = await _context.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == key);
    //    if (setting == null) return defaultValue;

    //    return setting.IsSensitive ? Util.Decrypt(setting.Value ?? string.Empty) : setting.Value ?? string.Empty;
    //}

    //public async Task SetSettingAsync(string key, string value, bool isSensitive = false, string prefix = "")
    //{
    //    if (string.IsNullOrEmpty(key)) throw new ArgumentException("Key cannot be empty", nameof(key));

    //    if (!string.IsNullOrEmpty(prefix))
    //    {
    //        prefix = $"{prefix.Trim(':')}:";
    //        key = $"{prefix}{key}";
    //    }

    //    using var transaction = await _context.Database.BeginTransactionAsync();
    //    try
    //    {
    //        var setting = await _context.Settings.FirstOrDefaultAsync(s => s.Key == key);
    //        if (setting == null)
    //        {
    //            setting = new Setting
    //            {
    //                Key = key,
    //                Value = isSensitive ? Util.Encrypt(value ?? string.Empty) : value ?? string.Empty,
    //                IsSensitive = isSensitive
    //            };
    //            _context.Settings.Add(setting);
    //        }
    //        else
    //        {
    //            setting.Value = isSensitive ? Util.Encrypt(value ?? string.Empty) : value ?? string.Empty;
    //            setting.IsSensitive = isSensitive;
    //        }

    //        await _context.SaveChangesAsync();
    //        await transaction.CommitAsync();

    //        _configProvider.TriggerReload();
    //    }
    //    catch
    //    {
    //        await transaction.RollbackAsync();
    //        throw;
    //    }
    //}
}