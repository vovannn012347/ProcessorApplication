using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace ProcessingModule.Services.Installation;

[JsonConverter(typeof(StringEnumConverter))]
public enum ReportLevel
{
    Info,
    Warning,
    Error
}

public sealed class ReportItem
{
    [JsonProperty("level")]
    public ReportLevel Level { get; init; }

    /// <summary>Which part of the package the message is about: archive, manifest, inputs, outputs, form, localization, hash, install.</summary>
    [JsonProperty("area")]
    public string Area { get; init; } = "";

    [JsonProperty("message")]
    public string Message { get; init; } = "";
}

/// <summary>Result of checking one script folder. Errors block installation; warnings don't.</summary>
public sealed class ScriptValidationReport
{
    [JsonProperty("items")]
    public List<ReportItem> Items { get; } = new();

    [JsonProperty("script_id")]
    public string? ScriptId { get; set; }

    [JsonProperty("version")]
    public string? Version { get; set; }

    [JsonProperty("label")]
    public string? Label { get; set; }

    [JsonProperty("entry_point")]
    public string? EntryPoint { get; set; }

    /// <summary>Hash of the files on disk, current method.</summary>
    [JsonProperty("computed_hash")]
    public string? ComputedHash { get; set; }

    [JsonProperty("hashed_file_count")]
    public int HashedFileCount { get; set; }

    [JsonProperty("file_count")]
    public int FileCount { get; set; }

    [JsonProperty("total_bytes")]
    public long TotalBytes { get; set; }

    [JsonProperty("has_errors")]
    public bool HasErrors => Items.Any(i => i.Level == ReportLevel.Error);

    [JsonProperty("warning_count")]
    public int WarningCount => Items.Count(i => i.Level == ReportLevel.Warning);

    public void Error(string area, string message) => Items.Add(new ReportItem { Level = ReportLevel.Error, Area = area, Message = message });
    public void Warn(string area, string message) => Items.Add(new ReportItem { Level = ReportLevel.Warning, Area = area, Message = message });
    public void Info(string area, string message) => Items.Add(new ReportItem { Level = ReportLevel.Info, Area = area, Message = message });
}
