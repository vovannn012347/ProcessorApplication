using System.Text.Json;
using System.Text.RegularExpressions;

using Common.Interfaces;
using Common.Models.Database;

using Microsoft.EntityFrameworkCore;

using ProcessorApplication.Database;
using ProcessorApplication.Infrastructure;
using ProcessorApplication.Utils;

namespace ProcessorApplication.Configuration.SettingsIngest;

/// <summary>One setting as the admin list shows it: the stored (database) value next to the settings files' value.</summary>
public class StoredSettingRow
{
    public string Area { get; set; } = string.Empty;
    /// <summary>Key inside the area, e.g. "P2PSettings:Endpoints:0".</summary>
    public string Key { get; set; } = string.Empty;
    /// <summary>First key segment ("P2PSettings"), the subsection the list is grouped by.</summary>
    public string Section { get; set; } = string.Empty;
    public bool IsStored { get; set; }
    public string? StoredValue { get; set; }
    public bool InFile { get; set; }
    public string? FileValue { get; set; }
    /// <summary>Passwords, tokens, secrets: values are never sent to the browser.</summary>
    public bool IsSensitive { get; set; }
    /// <summary>The value-less row SettingService keeps at a list's own path (marks the list as owned by the database).</summary>
    public bool IsListMarker { get; set; }
}

public interface ISettingsStore
{
    /// <summary>Every stored setting and every settings-file value of the modules, sorted by module, then key.</summary>
    Task<List<StoredSettingRow>> ListAsync(CancellationToken ct = default);

    /// <summary>Creates or replaces one stored value and reloads the options. Returns an error text or null.</summary>
    Task<string?> SetAsync(string area, string key, string? value, bool mustBeNew, CancellationToken ct = default);

    /// <summary>Removes one stored value (the settings file's value, if any, applies again) and reloads the options.</summary>
    Task<string?> DeleteAsync(string area, string key, CancellationToken ct = default);
}

public class SettingsStore : ISettingsStore
{
    private static readonly Regex SensitiveKey = new(
        @"(password|secret|token|apikey|api_key|privatekey|private_key|accesskey|connectionstring)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly JsonDocumentOptions JsonOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private readonly AppDbContext _db;
    private readonly ISettingService _settings;
    private readonly IEnumerable<IModule> _modules;
    private readonly IHostEnvironment _env;
    private readonly ILogger<SettingsStore> _logger;

    public SettingsStore(AppDbContext db, ISettingService settings, IEnumerable<IModule> modules, IHostEnvironment env, ILogger<SettingsStore> logger)
    {
        _db = db;
        _settings = settings;
        _modules = modules;
        _env = env;
        _logger = logger;
    }

    public static bool IsSensitiveKey(string key) => SensitiveKey.IsMatch(key);

    public IReadOnlyList<string> Areas => _modules.Select(m => m.ModuleId).OrderBy(a => a, StringComparer.OrdinalIgnoreCase).ToList();

    public async Task<List<StoredSettingRow>> ListAsync(CancellationToken ct = default)
    {
        var areas = _modules.Select(m => m.ModuleId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rows = new Dictionary<(string, string), StoredSettingRow>(new AreaKeyComparer());

        StoredSettingRow Row(string area, string key)
        {
            if (!rows.TryGetValue((area, key), out var r))
            {
                r = new StoredSettingRow { Area = area, Key = key, Section = key.Split(':')[0], IsSensitive = IsSensitiveKey(key) };
                rows[(area, key)] = r;
            }
            return r;
        }

        foreach (var s in await _db.Settings.AsNoTracking().ToListAsync(ct))
        {
            if (!areas.Contains(s.Area)) continue;
            var r = Row(s.Area, s.Key);
            r.IsStored = true;
            r.StoredValue = s.Value;
            r.IsListMarker = s.Value == null;
        }

        foreach (var (area, values) in ReadFileValues(areas))
        {
            foreach (var (key, value) in values)
            {
                var r = Row(area, key);
                r.InFile = true;
                r.FileValue = value;
            }
        }

        var list = rows.Values.ToList();
        list.Sort((a, b) =>
        {
            int c = string.Compare(a.Area, b.Area, StringComparison.OrdinalIgnoreCase);
            return c != 0 ? c : CompareKeys(a.Key, b.Key);
        });
        return list;
    }

    public async Task<string?> SetAsync(string area, string key, string? value, bool mustBeNew, CancellationToken ct = default)
    {
        var error = Validate(ref area, ref key);
        if (error != null) return error;

        var row = await _db.Settings.FirstOrDefaultAsync(s => s.Area == area && s.Key == key, ct);
        if (row != null && mustBeNew)
            return $"{area}:{key} already exists; edit it in the list.";
        if (row != null && row.Value == null)
            return "This is a list marker row (it keeps the list owned by the database); edit the list items instead.";
        if (await _db.Settings.AnyAsync(s => s.Area == area && s.Key.StartsWith(key + ":"), ct))
            return $"{area}:{key} is a section with values under it; set those values instead.";

        if (row == null)
            _db.Settings.Add(new Setting { Area = area, Key = key, Value = value ?? string.Empty });
        else
            row.Value = value ?? string.Empty;

        await _db.SaveChangesAsync(ct);
        _settings.ForceUpdateOptionsMonitor();
        _logger.LogInformation("[Settings] {Area}:{Key} {Action} on the settings list.", area, key, row == null ? "added" : "changed");
        return null;
    }

    public async Task<string?> DeleteAsync(string area, string key, CancellationToken ct = default)
    {
        var error = Validate(ref area, ref key);
        if (error != null) return error;

        var row = await _db.Settings.FirstOrDefaultAsync(s => s.Area == area && s.Key == key, ct);
        if (row == null) return $"{area}:{key} is not stored.";

        _db.Settings.Remove(row);
        await _db.SaveChangesAsync(ct);
        _settings.ForceUpdateOptionsMonitor();
        _logger.LogInformation("[Settings] {Area}:{Key} deleted on the settings list.", area, key);
        return null;
    }

    private string? Validate(ref string area, ref string key)
    {
        area = (area ?? string.Empty).Trim();
        key = (key ?? string.Empty).Trim().Trim(':');
        var a = area;
        var known = _modules.FirstOrDefault(m => string.Equals(m.ModuleId, a, StringComparison.OrdinalIgnoreCase));
        if (known == null) return $"Unknown module '{area}'.";
        area = known.ModuleId;
        if (key.Length == 0) return "The setting key is empty.";
        if (key.Contains("::") || key.Any(char.IsWhiteSpace)) return "A key is Section:Name, without spaces or empty parts.";
        return null;
    }

    /// <summary>The settings files' values per area, flattened like the configuration does ("Section:List:0").</summary>
    private Dictionary<string, Dictionary<string, string>> ReadFileValues(HashSet<string> areas)
    {
        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (area, path) in ModuleConfigurationExtensions.GetLayeredModuleJsonFiles(_env.EnvironmentName))
        {
            if (!areas.Contains(area)) continue;
            if (!result.TryGetValue(area, out var values))
                result[area] = values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path), JsonOptions);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                    Flatten(doc.RootElement, string.Empty, values);
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "[Settings] Could not read {File} for the settings list.", path);
            }
        }
        return result;
    }

    private static void Flatten(JsonElement e, string path, Dictionary<string, string> into)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var p in e.EnumerateObject())
                    Flatten(p.Value, path.Length == 0 ? p.Name : $"{path}:{p.Name}", into);
                break;
            case JsonValueKind.Array:
                // a list in a later file replaces the earlier one
                foreach (var k in into.Keys.Where(k => k.StartsWith(path + ":", StringComparison.OrdinalIgnoreCase)).ToList())
                    into.Remove(k);
                int i = 0;
                foreach (var item in e.EnumerateArray())
                    Flatten(item, $"{path}:{i++}", into);
                break;
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                break;
            case JsonValueKind.String:
                into[path] = e.GetString() ?? string.Empty;
                break;
            default:
                into[path] = e.ToString();
                break;
        }
    }

    /// <summary>Key order: segment by segment, numbers as numbers (so ":10" comes after ":9").</summary>
    private static int CompareKeys(string a, string b)
    {
        var x = a.Split(':');
        var y = b.Split(':');
        for (int i = 0; i < Math.Min(x.Length, y.Length); i++)
        {
            int c = int.TryParse(x[i], out var nx) && int.TryParse(y[i], out var ny)
                ? nx.CompareTo(ny)
                : string.Compare(x[i], y[i], StringComparison.OrdinalIgnoreCase);
            if (c != 0) return c;
        }
        return x.Length.CompareTo(y.Length);
    }

    private sealed class AreaKeyComparer : IEqualityComparer<(string, string)>
    {
        public bool Equals((string, string) a, (string, string) b) =>
            string.Equals(a.Item1, b.Item1, StringComparison.OrdinalIgnoreCase) && string.Equals(a.Item2, b.Item2, StringComparison.OrdinalIgnoreCase);
        public int GetHashCode((string, string) k) =>
            HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(k.Item1), StringComparer.OrdinalIgnoreCase.GetHashCode(k.Item2));
    }
}
