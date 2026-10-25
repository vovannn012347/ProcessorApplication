using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.Extensions.Options;

using ProcessorApplication.Configuration.Settings;
using ProcessorApplication.Database.Models;
using ProcessorApplication.Infrastructure;
using ProcessorApplication.Models;

namespace ProcessorApplication.Services.HashStamps;

/// <summary>
/// Backup of the server key chain, one JSON file per stamp. The database is not the only place the chain lives:
/// with these files and the node root key every key can be rebuilt (see <see cref="IHashStampService.RestoreFromBackupAsync"/>).
///
/// - Always written to the local folder (<see cref="SecuritySettings.LocalStampBackupPath"/>).
/// - Also written to <see cref="SecuritySettings.HashStampBackupFilePath"/> when backup is enabled, together with a copy
///   of the node root key, so a dead disk or a lost database can be recovered from there.
/// - Contains no plaintext key: derived keys are recomputed from the stamp time, legacy keys are stored wrapped.
/// </summary>
public class HashBackupService : IHashBackupService
{
    public const string DefaultLocalRelativePath = "Module_Data/Main/hashstamps";

    private readonly ILogger<HashBackupService> _logger;
    private readonly IOptionsMonitor<SecuritySettings> _settingsMonitor;
    private readonly INodeKeyService _nodeKey;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public HashBackupService(
        ILogger<HashBackupService> logger,
        IOptionsMonitor<SecuritySettings> settingsMonitor,
        INodeKeyService nodeKey)
    {
        _logger = logger;
        _settingsMonitor = settingsMonitor;
        _nodeKey = nodeKey;
    }

    private string LocalDirectory
    {
        get
        {
            string configured = _settingsMonitor.CurrentValue.LocalStampBackupPath;
            string path = string.IsNullOrWhiteSpace(configured) ? DefaultLocalRelativePath : configured;
            return Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(AppContext.BaseDirectory, path));
        }
    }

    private string? MirrorDirectory
    {
        get
        {
            var s = _settingsMonitor.CurrentValue;
            return s.HashStampBackupEnabled && !string.IsNullOrWhiteSpace(s.HashStampBackupFilePath)
                ? Path.GetFullPath(s.HashStampBackupFilePath)
                : null;
        }
    }

    private IEnumerable<string> Directories()
    {
        yield return LocalDirectory;
        if (MirrorDirectory is { } mirror && !string.Equals(mirror, LocalDirectory, StringComparison.OrdinalIgnoreCase))
            yield return mirror;
    }

    public void BackupServerBlock(ServerHashStamp stamp)
    {
        var entry = new HashBackupEntry
        {
            Id = stamp.Id,
            StampTime = stamp.StampTime,
            StampTimeText = ServerHashStamp.FormatStampTime(stamp.StampTime),
            KeyMode = stamp.KeyMode,
            KeyCheck = stamp.KeyCheck ?? "",
            WrappedKey = stamp.WrappedKey,
            BlockHash = HashStampService.ComputeSha256Hash(stamp.CalculateBlockContent()),
            PreviousBlockHash = stamp.PreviousHash,
            NodeKeyId = _nodeKey.KeyId
        };
        string json = JsonSerializer.Serialize(entry, JsonOptions);

        foreach (var dir in Directories())
        {
            try
            {
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, $"{stamp.Id}.json");
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, json, Encoding.UTF8);
                File.Move(tmp, path, overwrite: true);
            }
            catch (Exception ex)
            {
                _logger.LogCritical(ex, "Failed to write hash stamp backup for stamp {Id} to {Dir}", stamp.Id, dir);
            }
        }

        if (MirrorDirectory is { } mirrorDir) _nodeKey.CopyKeyFileTo(mirrorDir);
    }

    public HashBackupEntry GetBackupServerBlock(int id)
    {
        foreach (var dir in Directories())
        {
            var entry = Read(Path.Combine(dir, $"{id}.json"));
            if (entry != null) return entry;
        }

        _logger.LogWarning("No backup file found for hash stamp {Id}", id);
        return null;
    }

    public List<HashBackupEntry> GetAllBackupEntries()
    {
        var byId = new Dictionary<int, HashBackupEntry>();
        foreach (var dir in Directories().Reverse()) // local last, so it wins
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var file in Directory.GetFiles(dir, "*.json"))
            {
                var entry = Read(file);
                if (entry != null) byId[entry.Id] = entry;
            }
        }
        return byId.Values.OrderBy(e => e.StampTime).ToList();
    }

    private HashBackupEntry? Read(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var entry = JsonSerializer.Deserialize<HashBackupEntry>(File.ReadAllText(path, Encoding.UTF8), JsonOptions);
            // Old-format files (v1) stored the plaintext key as BlockHash; they carry no StampTimeText.
            return entry is { FormatVersion: >= 2 } ? entry : null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read hash stamp backup {Path}", path);
            return null;
        }
    }
}
