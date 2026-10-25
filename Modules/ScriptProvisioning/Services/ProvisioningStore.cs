using System.Text.Json;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using ScriptProvisioning.Configuration;
using ScriptProvisioning.Models;

namespace ScriptProvisioning.Services;

/// <summary>
/// Small JSON files in the module data folder: what this node publishes (published.json) and where
/// installed builds came from (installed.json). Not in a database: a handful of rows, read on page load.
/// </summary>
public interface IProvisioningStore
{
    string DataDirectory { get; }
    string PublishedDirectory { get; }

    List<PublishedPackage> GetPublished();
    void SavePublished(PublishedPackage package);
    void RemovePublished(int scriptIndexId);

    List<InstallRecord> GetInstallRecords();
    void AddInstallRecord(InstallRecord record);
}

public sealed class ProvisioningStore : IProvisioningStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly object _lock = new();
    private readonly IOptionsMonitor<ScriptProvisioningSettings> _settings;
    private readonly ILogger<ProvisioningStore> _logger;

    public ProvisioningStore(IOptionsMonitor<ScriptProvisioningSettings> settings, ILogger<ProvisioningStore> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    public string DataDirectory
    {
        get
        {
            string path = _settings.CurrentValue.DataPath;
            if (string.IsNullOrWhiteSpace(path)) path = "Module_Data/ScriptProvisioning";
            return Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(AppContext.BaseDirectory, path));
        }
    }

    public string PublishedDirectory => Path.Combine(DataDirectory, "published");

    private string PublishedFile => Path.Combine(DataDirectory, "published.json");
    private string InstalledFile => Path.Combine(DataDirectory, "installed.json");

    public List<PublishedPackage> GetPublished()
    {
        lock (_lock) return Read<PublishedPackage>(PublishedFile);
    }

    public void SavePublished(PublishedPackage package)
    {
        lock (_lock)
        {
            var list = Read<PublishedPackage>(PublishedFile);
            list.RemoveAll(p => p.ScriptIndexId == package.ScriptIndexId);
            list.Add(package);
            Write(PublishedFile, list.OrderBy(p => p.ScriptId, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Version).ToList());
        }
    }

    public void RemovePublished(int scriptIndexId)
    {
        lock (_lock)
        {
            var list = Read<PublishedPackage>(PublishedFile);
            if (list.RemoveAll(p => p.ScriptIndexId == scriptIndexId) > 0) Write(PublishedFile, list);
        }
    }

    public List<InstallRecord> GetInstallRecords()
    {
        lock (_lock) return Read<InstallRecord>(InstalledFile);
    }

    public void AddInstallRecord(InstallRecord record)
    {
        lock (_lock)
        {
            var list = Read<InstallRecord>(InstalledFile);
            list.Add(record);
            Write(InstalledFile, list);
        }
    }

    private List<T> Read<T>(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<List<T>>(File.ReadAllText(path), Json) ?? new() : new();
        }
        catch (Exception ex)
        {
            // Keep the broken file for inspection; start over rather than failing the page
            _logger.LogError(ex, "[ScriptProvisioning] Could not read {Path}; keeping it as .broken", path);
            try { File.Copy(path, path + ".broken", overwrite: true); } catch { /* best effort */ }
            return new();
        }
    }

    private static void Write<T>(string path, List<T> items)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(items, Json));
        File.Move(path + ".tmp", path, overwrite: true);
    }
}
