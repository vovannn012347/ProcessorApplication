using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using Common.Interfaces;
using Common.Models.Database;

using Microsoft.EntityFrameworkCore;

using ProcessorApplication.Database;
using ProcessorApplication.Infrastructure;
using ProcessorApplication.Utils;

namespace ProcessorApplication.Configuration.SettingsIngest;

/// <summary>
/// What a re-ingest run does with values from the module appsettings files.
/// </summary>
public enum SettingsIngestMode
{
    /// <summary>Nothing (settings files are only read for keys the DB doesn't have yet, as before).</summary>
    Off,

    /// <summary>
    /// Apply only the values that changed in a settings file since the last run. Values changed in the app
    /// (settings pages) are kept as long as the file value for them stays the same. Default.
    /// </summary>
    Changed,

    /// <summary>Settings files win: every value present in a file replaces the stored one.</summary>
    Overwrite
}

/// <summary>
/// Startup behaviour, read from the root configuration (appsettings.json, appsettings.{env}.json,
/// environment variables <c>SettingsIngest__Mode</c>, command line <c>--SettingsIngest:Mode=Overwrite</c>).
/// Deliberately not a module setting: it must not end up in, or be overridden by, the settings DB.
/// </summary>
public class SettingsIngestOptions
{
    public const string SectionName = "SettingsIngest";

    public SettingsIngestMode Mode { get; set; } = SettingsIngestMode.Changed;

    /// <summary>
    /// Remembers the file values of the last run (needed for <see cref="SettingsIngestMode.Changed"/>).
    /// Relative paths are relative to the working directory, like the settings database itself.
    /// </summary>
    public string StateFilePath { get; set; } = "./Module_Data/Main/settings_ingest_state.json";
}

public enum SettingsIngestStatus
{
    /// <summary>The file value changed since the last run; applied by Changed and Overwrite.</summary>
    ChangedInFile,

    /// <summary>The stored value was changed in the app; the file value didn't change. Applied only by Overwrite.</summary>
    ChangedInApp,

    /// <summary>File and DB differ and there is no record of a previous run for this value. Applied only by Overwrite.</summary>
    Untracked
}

public class SettingsIngestDifference
{
    public string Area { get; set; } = string.Empty;
    /// <summary>Setting key inside the area, e.g. "P2PSettings:Endpoints" (a whole list) or "P2PSettings:StartupDelaySeconds".</summary>
    public string Key { get; set; } = string.Empty;
    public bool IsList { get; set; }
    public SettingsIngestStatus Status { get; set; }
    public string? StoredValue { get; set; }
    public string? FileValue { get; set; }
    /// <summary>Values are masked in <see cref="StoredValue"/>/<see cref="FileValue"/> (passwords, tokens, secrets).</summary>
    public bool IsSensitive { get; set; }
    public bool Applied { get; set; }
}

public class SettingsIngestResult
{
    public SettingsIngestMode Mode { get; set; }
    public bool IsPreview { get; set; }
    public string? AreaFilter { get; set; }
    public List<string> Files { get; } = new();
    public List<SettingsIngestDifference> Differences { get; } = new();
    public List<string> Errors { get; } = new();
    public int AppliedCount => Differences.Count(d => d.Applied);
    public bool HadStateBefore { get; set; }
}

public interface ISettingsIngestService
{
    /// <summary>Lists what differs between the settings files and the stored settings; changes nothing.</summary>
    Task<SettingsIngestResult> PreviewAsync(string? area = null, CancellationToken ct = default);

    /// <summary>
    /// Applies file values according to <paramref name="mode"/> (optionally for one area only), records the
    /// file values for the next <see cref="SettingsIngestMode.Changed"/> run and reloads the options.
    /// </summary>
    Task<SettingsIngestResult> ApplyAsync(SettingsIngestMode mode, string? area = null, CancellationToken ct = default);
}

/// <summary>
/// Re-ingests module settings from the appsettings files into the settings DB.
///
/// Normally the DB is authoritative: ConfigurationBootstrapper only copies keys the DB doesn't have yet, so editing
/// a settings file after the first start does nothing. This service makes file edits take effect again, without
/// losing values changed on the settings pages: it remembers the file values of the last run in a small state file
/// and, in <see cref="SettingsIngestMode.Changed"/> mode, applies only what changed in a file since then.
///
/// Rules:
/// - The files are exactly the ones the configuration loader reads (<see cref="ModuleConfigurationExtensions.GetModuleJsonFiles"/>);
///   an environment file overrides the base file of the same area.
/// - A list is one unit: a list in a file replaces the stored list completely (an empty list clears it).
///   An environment file's list replaces the base file's list (not index by index).
/// - Empty strings and nulls in a file are never applied (same as the first-start seeding), so generated values
///   like identifiers, passwords and secrets that the files leave empty are never wiped.
/// - Keys that exist only in the DB are left alone; nothing is deleted except list items replaced by a file list.
/// - Only areas of loaded modules are touched.
/// </summary>
public class SettingsIngestService : ISettingsIngestService
{
    private static readonly Regex SensitiveKey = new(
        @"(password|secret|token|apikey|api_key|privatekey|private_key|accesskey|connectionstring)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly JsonDocumentOptions JsonOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private static readonly JsonSerializerOptions StateJsonOptions = new() { WriteIndented = true };

    private readonly AppDbContext _db;
    private readonly ISettingService _settings;
    private readonly IEnumerable<IModule> _modules;
    private readonly IHostEnvironment _env;
    private readonly IConfiguration _config;
    private readonly ILogger<SettingsIngestService> _logger;

    public SettingsIngestService(
        AppDbContext db,
        ISettingService settings,
        IEnumerable<IModule> modules,
        IHostEnvironment env,
        IConfiguration config,
        ILogger<SettingsIngestService> logger)
    {
        _db = db;
        _settings = settings;
        _modules = modules;
        _env = env;
        _config = config;
        _logger = logger;
    }

    public SettingsIngestOptions Options =>
        _config.GetSection(SettingsIngestOptions.SectionName).Get<SettingsIngestOptions>() ?? new SettingsIngestOptions();

    public Task<SettingsIngestResult> PreviewAsync(string? area = null, CancellationToken ct = default)
        => RunAsync(SettingsIngestMode.Off, area, preview: true, ct);

    public Task<SettingsIngestResult> ApplyAsync(SettingsIngestMode mode, string? area = null, CancellationToken ct = default)
        => RunAsync(mode, area, preview: false, ct);

    private async Task<SettingsIngestResult> RunAsync(SettingsIngestMode mode, string? areaFilter, bool preview, CancellationToken ct)
    {
        var result = new SettingsIngestResult { Mode = mode, IsPreview = preview, AreaFilter = areaFilter };
        if (!preview && mode == SettingsIngestMode.Off)
            return result;

        var moduleIds = _modules.Select(m => m.ModuleId).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // 1. What the files say, per area (environment file overrides base file).
        var fileUnits = new Dictionary<string, Dictionary<string, SettingUnit>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (area, filePath) in ModuleConfigurationExtensions.GetModuleJsonFiles(AppContext.BaseDirectory, _env.EnvironmentName))
        {
            if (!moduleIds.Contains(area))
                continue;
            if (areaFilter != null && !string.Equals(area, areaFilter, StringComparison.OrdinalIgnoreCase))
                continue;

            result.Files.Add(Path.GetRelativePath(AppContext.BaseDirectory, filePath));
            try
            {
                var units = ReadUnits(await File.ReadAllTextAsync(filePath, ct));
                if (!fileUnits.TryGetValue(area, out var areaUnits))
                    fileUnits[area] = areaUnits = new Dictionary<string, SettingUnit>(StringComparer.OrdinalIgnoreCase);

                foreach (var unit in units)
                {
                    // A list from a later file replaces the earlier list (and anything stored under that path).
                    if (unit.IsList)
                    {
                        foreach (var covered in areaUnits.Keys.Where(k => IsSameOrChild(k, unit.Key)).ToList())
                            areaUnits.Remove(covered);
                    }
                    areaUnits[unit.Key] = unit;
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                // A broken file must not be half-applied: skip the whole area.
                result.Errors.Add($"{Path.GetFileName(filePath)}: {ex.Message} (area {area} skipped)");
                _logger.LogError(ex, "[Settings ingest] Could not read {File}; area {Area} skipped.", filePath, area);
                fileUnits.Remove(area);
                moduleIds.Remove(area);
            }
        }

        // 2. What the DB holds for those areas.
        var areas = fileUnits.Keys.ToList();
        var rows = await _db.Settings.Where(s => areas.Contains(s.Area)).ToListAsync(ct);
        var rowsByArea = rows
            .GroupBy(r => r.Area, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        // 3. What the files said last time.
        var options = Options;
        var state = LoadState(options.StateFilePath, result);
        result.HadStateBefore = state.Count > 0;

        bool dbChanged = false;
        foreach (var (area, units) in fileUnits)
        {
            var areaRows = rowsByArea.TryGetValue(area, out var r) ? r : new List<Setting>();

            foreach (var unit in units.Values)
            {
                if (!unit.HasApplicableValue)
                    continue; // e.g. "EnforcedNodeIdentifier": "" - never wipe a stored/generated value

                var stored = StoredLeaves(areaRows, unit);
                if (LeavesEquivalent(unit.Leaves, stored))
                    continue;

                var stateKey = $"{area}:{unit.Key}";
                var status = !state.TryGetValue(stateKey, out var previous)
                    ? SettingsIngestStatus.Untracked
                    : previous == unit.Fingerprint ? SettingsIngestStatus.ChangedInApp : SettingsIngestStatus.ChangedInFile;

                bool apply = !preview && (mode == SettingsIngestMode.Overwrite
                                          || (mode == SettingsIngestMode.Changed && status == SettingsIngestStatus.ChangedInFile));

                bool sensitive = SensitiveKey.IsMatch(unit.Key);
                result.Differences.Add(new SettingsIngestDifference
                {
                    Area = area,
                    Key = unit.Key,
                    IsList = unit.IsList,
                    Status = status,
                    IsSensitive = sensitive,
                    StoredValue = Describe(stored, unit, sensitive),
                    FileValue = Describe(unit.Leaves, unit, sensitive),
                    Applied = apply
                });

                if (apply)
                {
                    ApplyUnit(area, unit, areaRows);
                    dbChanged = true;
                    _logger.LogInformation("[Settings ingest] {Area}:{Key} set from settings file ({Status}).", area, unit.Key, status);
                }
            }
        }

        if (preview)
            return result;

        if (dbChanged)
        {
            await _db.SaveChangesAsync(ct);
            _settings.ForceUpdateOptionsMonitor();
        }

        // 4. Remember the current file values (for the processed areas) for the next Changed run.
        foreach (var (area, units) in fileUnits)
        {
            foreach (var key in state.Keys.Where(k => k.StartsWith(area + ":", StringComparison.OrdinalIgnoreCase)).ToList())
                state.Remove(key);
            foreach (var unit in units.Values)
                state[$"{area}:{unit.Key}"] = unit.Fingerprint;
        }
        SaveState(options.StateFilePath, state, result);

        _logger.LogInformation(
            "[Settings ingest] Mode {Mode}: {Applied} of {Diff} differing settings applied from {Files} file(s){First}.",
            mode, result.AppliedCount, result.Differences.Count, result.Files.Count,
            result.HadStateBefore ? "" : " (first run: file values recorded, only Overwrite applies untracked differences)");

        return result;
    }

    #region File reading

    /// <summary>One thing that is applied as a whole: a single value, or a complete list.</summary>
    private sealed class SettingUnit
    {
        public string Key { get; init; } = string.Empty;
        public bool IsList { get; init; }
        /// <summary>Flattened key -> value, keys relative to the area ("P2PSettings:Endpoints:0").</summary>
        public SortedDictionary<string, string?> Leaves { get; } = new(StringComparer.OrdinalIgnoreCase);

        public bool HasApplicableValue => IsList || Leaves.Values.Any(v => !string.IsNullOrEmpty(v));

        /// <summary>
        /// SHA-256 of the applicable values. Only the hash is kept in the state file, so it never holds a
        /// second plain copy of passwords or tokens from the settings files.
        /// </summary>
        public string Fingerprint => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
            Leaves.Where(l => !string.IsNullOrEmpty(l.Value)).Select(l => new[] { l.Key.ToLowerInvariant(), l.Value })))));
    }

    private static List<SettingUnit> ReadUnits(string json)
    {
        using var doc = JsonDocument.Parse(json, JsonOptions);
        var units = new List<SettingUnit>();
        if (doc.RootElement.ValueKind == JsonValueKind.Object)
            Visit(doc.RootElement, string.Empty, units);
        return units;
    }

    private static void Visit(JsonElement element, string path, List<SettingUnit> units)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in element.EnumerateObject())
                    Visit(prop.Value, path.Length == 0 ? prop.Name : $"{path}:{prop.Name}", units);
                break;

            case JsonValueKind.Array:
                var list = new SettingUnit { Key = path, IsList = true };
                FlattenInto(element, path, list.Leaves);
                units.Add(list);
                break;

            default:
                if (path.Length == 0) return;
                var single = new SettingUnit { Key = path };
                single.Leaves[path] = LeafValue(element);
                units.Add(single);
                break;
        }
    }

    /// <summary>Same flattening as the configuration loader (colon paths, list indexes as segments).</summary>
    private static void FlattenInto(JsonElement element, string path, IDictionary<string, string?> leaves)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in element.EnumerateObject())
                    FlattenInto(prop.Value, $"{path}:{prop.Name}", leaves);
                break;
            case JsonValueKind.Array:
                int i = 0;
                foreach (var item in element.EnumerateArray())
                    FlattenInto(item, $"{path}:{i++}", leaves);
                break;
            default:
                leaves[path] = LeafValue(element);
                break;
        }
    }

    // Matches ModuleConfigurationExtensions.Flatten (JsonElement.ToString(): "True"/"False", raw numbers).
    private static string? LeafValue(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.String => element.GetString(),
        _ => element.ToString()
    };

    #endregion

    #region DB comparison / apply

    private static bool IsSameOrChild(string key, string path) =>
        string.Equals(key, path, StringComparison.OrdinalIgnoreCase)
        || key.StartsWith(path + ":", StringComparison.OrdinalIgnoreCase);

    private static SortedDictionary<string, string?> StoredLeaves(List<Setting> areaRows, SettingUnit unit)
    {
        var stored = new SortedDictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in areaRows)
        {
            bool belongs = unit.IsList
                ? row.Key.StartsWith(unit.Key + ":", StringComparison.OrdinalIgnoreCase) // the list marker row (key itself, value null) is not a value
                : string.Equals(row.Key, unit.Key, StringComparison.OrdinalIgnoreCase);
            if (belongs && row.Value != null)
                stored[row.Key] = row.Value;
        }
        return stored;
    }

    private static bool LeavesEquivalent(IDictionary<string, string?> file, IDictionary<string, string?> stored)
    {
        var f = file.Where(l => !string.IsNullOrEmpty(l.Value)).ToList();
        var s = stored.Where(l => !string.IsNullOrEmpty(l.Value)).ToList();
        if (f.Count != s.Count)
            return false;

        foreach (var (key, value) in f)
        {
            var match = s.FirstOrDefault(x => string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase));
            if (match.Key == null || !ValuesEquivalent(value, match.Value))
                return false;
        }
        return true;
    }

    /// <summary>"True" from a file equals "true" saved by a settings page; "5" equals "5.0".</summary>
    private static bool ValuesEquivalent(string? a, string? b)
    {
        if (string.Equals(a, b, StringComparison.Ordinal))
            return true;
        if (a == null || b == null)
            return false;
        if (bool.TryParse(a, out var ba) && bool.TryParse(b, out var bb))
            return ba == bb;
        if (decimal.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out var da)
            && decimal.TryParse(b, NumberStyles.Float, CultureInfo.InvariantCulture, out var db))
            return da == db;
        return false;
    }

    private void ApplyUnit(string area, SettingUnit unit, List<Setting> areaRows)
    {
        var wanted = unit.Leaves
            .Where(l => !string.IsNullOrEmpty(l.Value))
            .ToDictionary(l => l.Key, l => l.Value, StringComparer.OrdinalIgnoreCase);

        if (unit.IsList)
        {
            // The list's own path gets a value-less marker row, as SettingService does for lists, so
            // ConfigurationBootstrapper treats the list as owned by the DB (an empty list stays empty).
            wanted[unit.Key] = null;
        }

        var existing = areaRows.Where(r => IsSameOrChild(r.Key, unit.Key)).ToList();

        // Update rows in place where the key already exists (the (Key, Area) index is unique),
        // remove the rest, insert what's missing.
        foreach (var row in existing)
        {
            if (wanted.TryGetValue(row.Key, out var value))
            {
                row.Value = value;
                wanted.Remove(row.Key);
            }
            else if (unit.IsList)
            {
                _db.Settings.Remove(row);
                areaRows.Remove(row);
            }
        }

        foreach (var (key, value) in wanted)
        {
            var row = new Setting { Area = area, Key = key, Value = value };
            _db.Settings.Add(row);
            areaRows.Add(row);
        }
    }

    private static string? Describe(IDictionary<string, string?> leaves, SettingUnit unit, bool sensitive)
    {
        var values = leaves.Where(l => !string.IsNullOrEmpty(l.Value)).ToList();
        if (sensitive)
            return values.Count == 0 ? (unit.IsList ? "[]" : null) : "•••• (hidden)";
        if (!unit.IsList)
            return values.Count == 0 ? null : values[0].Value;
        if (values.Count == 0)
            return "[]";

        // Plain list ("Key:0", "Key:1"...): [a, b]. List of objects: one "index:field = value" per line.
        var prefix = unit.Key + ":";
        bool plain = values.All(v => v.Key.Length > prefix.Length
                                     && v.Key.Substring(prefix.Length).All(char.IsDigit));
        if (plain)
            return "[" + string.Join(", ", values.Select(v => v.Value)) + "]";

        var sb = new StringBuilder();
        foreach (var v in values)
            sb.AppendLine($"{v.Key.Substring(Math.Min(prefix.Length, v.Key.Length))} = {v.Value}");
        return sb.ToString().TrimEnd();
    }

    #endregion

    #region State file

    private Dictionary<string, string> LoadState(string path, SettingsIngestResult result)
    {
        try
        {
            if (File.Exists(path))
            {
                var data = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
                if (data != null)
                    return new Dictionary<string, string>(data, StringComparer.OrdinalIgnoreCase);
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            result.Errors.Add($"Ingest state file unreadable ({ex.Message}); treated as a first run.");
            _logger.LogWarning(ex, "[Settings ingest] State file {Path} unreadable; treated as a first run.", path);
        }
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    private void SaveState(string path, Dictionary<string, string> state, SettingsIngestResult result)
    {
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var sorted = new SortedDictionary<string, string>(state, StringComparer.OrdinalIgnoreCase);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(sorted, StateJsonOptions));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            result.Errors.Add($"Could not save the ingest state file ({ex.Message}); the next Changed run will see untracked values.");
            _logger.LogWarning(ex, "[Settings ingest] Could not write state file {Path}.", path);
        }
    }

    #endregion
}

/// <summary>Runs the configured re-ingest once at startup, right after the first-start seeding.</summary>
public static class SettingsIngestStartup
{
    public static async Task RunAsync(WebApplication app)
    {
        using var scope = app.Services.CreateScope();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(SettingsIngestStartup));
        var service = scope.ServiceProvider.GetRequiredService<ISettingsIngestService>();

        var options = app.Configuration.GetSection(SettingsIngestOptions.SectionName).Get<SettingsIngestOptions>()
                      ?? new SettingsIngestOptions();
        if (options.Mode == SettingsIngestMode.Off)
        {
            logger.LogInformation("[Settings ingest] Off; settings files are only used for keys the database doesn't have yet.");
            return;
        }

        try
        {
            var result = await service.ApplyAsync(options.Mode);
            foreach (var error in result.Errors)
                logger.LogWarning("[Settings ingest] {Error}", error);
        }
        catch (Exception ex)
        {
            // Never block startup over this; the stored settings are still valid.
            logger.LogError(ex, "[Settings ingest] Startup re-ingest failed; continuing with the stored settings.");
        }
    }
}
