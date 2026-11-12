using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Common.Infrastructure;

using Microsoft.Extensions.Options;

using ProcessorApplication.Configuration.Settings;
using ProcessorApplication.Database.Models;

namespace ProcessorApplication.Services.HashStamps;

/// <summary>A problem found in the key chain files themselves (not in the chain's links).</summary>
public sealed record KeyChainFileProblem(string File, string Reason, string Detail);

/// <summary>
/// Where the server key chain lives: files in the node home, not the database.
/// <para>
/// One file per stamp (<c>{id:D8}.stamp</c>) plus <c>head.dat</c>, in <see cref="SecuritySettings.KeyChainPath"/>
/// (default <c>Module_Data/Main/keychain</c>). Every file is AES-GCM encrypted with a key derived from the node root key
/// (<see cref="INodeKeyService.DeriveStorageKey"/>), with the file's own name as associated data: a file that was
/// edited, swapped with another stamp's file or renamed fails to open. The head file records the last stamp and its
/// block hash, so a removed last stamp is noticed too (the chain links alone can't show a missing tail).
/// </para>
/// <para>
/// The files hold no key in plain: derived keys are recomputed from the stamp time, old random keys are kept wrapped
/// with the root key. The whole chain is small (one stamp per key period) and is kept in memory after the first read.
/// </para>
/// </summary>
public interface IHashStampStore
{
    /// <summary>The folder of the chain files.</summary>
    string Folder { get; }

    /// <summary>True if any stamp file exists (checked without the root key).</summary>
    bool HasFiles { get; }

    /// <summary>All readable stamps, oldest first (MasterKey not filled in).</summary>
    IReadOnlyList<ServerHashStamp> GetAll();

    ServerHashStamp? GetLatest();

    /// <summary>The stamp active at <paramref name="utcTime"/> (newest with StampTime &lt;= time), or null.</summary>
    ServerHashStamp? GetAt(DateTime utcTime);

    ServerHashStamp? GetById(int id);

    /// <summary>Appends a new stamp; assigns its Id (last + 1) and moves the head.</summary>
    ServerHashStamp Append(ServerHashStamp stamp);

    /// <summary>Writes a stamp under its existing Id (migration from the database, restore from backups, key conversion).</summary>
    void Put(ServerHashStamp stamp);

    /// <summary>Files that couldn't be opened, a missing tail, a head that doesn't match - found while loading.</summary>
    IReadOnlyList<KeyChainFileProblem> Problems { get; }
}

public sealed class HashStampFileStore : IHashStampStore
{
    public const string DefaultRelativePath = "Module_Data/Main/keychain";
    private const string StorePurpose = "keychain";
    private const string FileMagic = "PSTAMP1";
    private const string HeadFileName = "head.dat";
    private const string TimeFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";

    private readonly IOptionsMonitor<SecuritySettings> _settings;
    private readonly INodeKeyService _nodeKey;
    private readonly ILogger<HashStampFileStore> _logger;
    private readonly object _lock = new();

    private List<ServerHashStamp>? _stamps;
    private readonly List<KeyChainFileProblem> _problems = new();
    private byte[]? _fileKey;

    public HashStampFileStore(IOptionsMonitor<SecuritySettings> settings, INodeKeyService nodeKey, ILogger<HashStampFileStore> logger)
    {
        _settings = settings;
        _nodeKey = nodeKey;
        _logger = logger;
    }

    private sealed class StampRecord
    {
        public int Version { get; set; } = 1;
        public int Id { get; set; }
        public string StampTime { get; set; } = "";
        public StampKeyMode KeyMode { get; set; }
        public string? KeyCheck { get; set; }
        public string? WrappedKey { get; set; }
        public string PreviousHash { get; set; } = "";
    }

    private sealed class HeadRecord
    {
        public int Version { get; set; } = 1;
        public int LastId { get; set; }
        public string LastBlockHash { get; set; } = "";
        public int Count { get; set; }
        public DateTime UpdatedUtc { get; set; }
    }

    public string Folder
    {
        get
        {
            string configured = _settings.CurrentValue.KeyChainPath;
            return NodeHome.Resolve(string.IsNullOrWhiteSpace(configured) ? DefaultRelativePath : configured);
        }
    }

    public bool HasFiles => Directory.Exists(Folder) && Directory.EnumerateFiles(Folder, "*.stamp").Any();

    public IReadOnlyList<KeyChainFileProblem> Problems
    {
        get { lock (_lock) { EnsureLoaded(); return _problems.ToList(); } }
    }

    public IReadOnlyList<ServerHashStamp> GetAll()
    {
        lock (_lock) { EnsureLoaded(); return _stamps!.Select(Clone).ToList(); }
    }

    public ServerHashStamp? GetLatest()
    {
        lock (_lock) { EnsureLoaded(); return _stamps!.Count == 0 ? null : Clone(_stamps[^1]); }
    }

    public ServerHashStamp? GetAt(DateTime utcTime)
    {
        lock (_lock)
        {
            EnsureLoaded();
            ServerHashStamp? found = null;
            foreach (var s in _stamps!)
            {
                if (s.StampTime <= utcTime) found = s; else break;
            }
            return found == null ? null : Clone(found);
        }
    }

    public ServerHashStamp? GetById(int id)
    {
        lock (_lock) { EnsureLoaded(); var s = _stamps!.FirstOrDefault(x => x.Id == id); return s == null ? null : Clone(s); }
    }

    public ServerHashStamp Append(ServerHashStamp stamp)
    {
        lock (_lock)
        {
            EnsureLoaded();
            int id = _stamps!.Count == 0 ? 1 : _stamps.Max(s => s.Id) + 1;
            var copy = Clone(stamp);
            copy.Id = id;
            WriteStamp(copy);
            _stamps.Add(copy);
            Sort();
            WriteHead();
            stamp.Id = id;
            return Clone(copy);
        }
    }

    public void Put(ServerHashStamp stamp)
    {
        lock (_lock)
        {
            EnsureLoaded();
            if (stamp.Id <= 0) throw new ArgumentException("Put needs the stamp's Id; use Append for a new stamp.");
            var copy = Clone(stamp);
            WriteStamp(copy);
            _stamps!.RemoveAll(s => s.Id == copy.Id);
            _stamps.Add(copy);
            Sort();
            WriteHead();
        }
    }

    // ---------------------------------------------------------------- loading

    private void EnsureLoaded()
    {
        if (_stamps != null) return;

        // Creating the root key is HashStampService's decision (it also knows about stamps still in the old database).
        _nodeKey.EnsureLoaded(allowCreate: false);
        _fileKey = _nodeKey.DeriveStorageKey(StorePurpose);

        var stamps = new List<ServerHashStamp>();
        _problems.Clear();
        string folder = Folder;

        if (Directory.Exists(folder))
        {
            foreach (var path in Directory.EnumerateFiles(folder, "*.stamp"))
            {
                string name = Path.GetFileName(path);
                try
                {
                    var record = JsonSerializer.Deserialize<StampRecord>(Decrypt(path, name))
                                 ?? throw new InvalidDataException("empty record");
                    string expectedName = FileNameFor(record.Id);
                    if (!string.Equals(name, expectedName, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException($"file holds stamp {record.Id} (expected name {expectedName})");
                    stamps.Add(FromRecord(record));
                }
                catch (Exception ex)
                {
                    _problems.Add(new KeyChainFileProblem(name, "FILE_UNREADABLE", ex.Message));
                    _logger.LogCritical("[Key chain] {File} can't be opened ({Msg}). It was changed, damaged, or belongs to another root key.", name, ex.Message);
                }
            }
        }

        _stamps = stamps;
        Sort();
        CheckHead(folder);
    }

    private void CheckHead(string folder)
    {
        string path = Path.Combine(folder, HeadFileName);
        if (!File.Exists(path))
        {
            if (_stamps!.Count > 0)
            {
                _problems.Add(new KeyChainFileProblem(HeadFileName, "HEAD_MISSING", "rebuilt from the stamp files"));
                _logger.LogWarning("[Key chain] {Head} is missing; rebuilding it from {Count} stamp file(s).", HeadFileName, _stamps.Count);
                WriteHead();
            }
            return;
        }

        HeadRecord? head;
        try
        {
            head = JsonSerializer.Deserialize<HeadRecord>(Decrypt(path, HeadFileName));
        }
        catch (Exception ex)
        {
            _problems.Add(new KeyChainFileProblem(HeadFileName, "HEAD_UNREADABLE", ex.Message));
            _logger.LogCritical("[Key chain] {Head} can't be opened ({Msg}).", HeadFileName, ex.Message);
            return;
        }
        if (head == null) return;

        int lastId = _stamps!.Count == 0 ? 0 : _stamps[^1].Id;
        if (lastId < head.LastId)
        {
            _problems.Add(new KeyChainFileProblem(HeadFileName, "TAIL_MISSING",
                $"head says the last stamp is {head.LastId}, the files end at {lastId}"));
            _logger.LogCritical("[Key chain] Stamp(s) after {Last} are missing: the head records stamp {HeadLast}. Restore them from the backups.", lastId, head.LastId);
        }
        else if (lastId > head.LastId)
        {
            // A stamp written but the head not updated (stopped in between): accept and move the head.
            _logger.LogWarning("[Key chain] Head was behind (stamp {HeadLast} -> {Last}); updated.", head.LastId, lastId);
            WriteHead();
        }
        else if (lastId > 0 && !string.Equals(head.LastBlockHash, BlockHashOf(_stamps[^1]), StringComparison.OrdinalIgnoreCase))
        {
            _problems.Add(new KeyChainFileProblem(HeadFileName, "HEAD_MISMATCH", $"last stamp {lastId} differs from the one the head recorded"));
            _logger.LogCritical("[Key chain] The last stamp ({Last}) is not the one the head recorded.", lastId);
        }
    }

    // ---------------------------------------------------------------- files

    private static string FileNameFor(int id) => id.ToString("D8", CultureInfo.InvariantCulture) + ".stamp";

    private static string BlockHashOf(ServerHashStamp s) => HashStampService.ComputeSha256Hash(s.CalculateBlockContent());

    private void WriteStamp(ServerHashStamp stamp)
    {
        var record = new StampRecord
        {
            Id = stamp.Id,
            StampTime = ServerHashStamp.FormatStampTime(stamp.StampTime),
            KeyMode = stamp.KeyMode,
            KeyCheck = stamp.KeyCheck,
            WrappedKey = stamp.WrappedKey,
            PreviousHash = stamp.PreviousHash
        };
        if (stamp.KeyMode == StampKeyMode.LegacyPlain)
            throw new InvalidOperationException("Plaintext keys are never written to the key chain files; wrap them first.");

        string name = FileNameFor(stamp.Id);
        WriteEncrypted(Path.Combine(Folder, name), name, JsonSerializer.Serialize(record));
    }

    private void WriteHead()
    {
        var last = _stamps!.Count == 0 ? null : _stamps[^1];
        var head = new HeadRecord
        {
            LastId = last?.Id ?? 0,
            LastBlockHash = last == null ? "" : BlockHashOf(last),
            Count = _stamps.Count,
            UpdatedUtc = DateTime.UtcNow
        };
        WriteEncrypted(Path.Combine(Folder, HeadFileName), HeadFileName, JsonSerializer.Serialize(head));
    }

    private void WriteEncrypted(string path, string associatedName, string json)
    {
        byte[] plain = Encoding.UTF8.GetBytes(json);
        byte[] nonce = RandomNumberGenerator.GetBytes(12);
        byte[] cipher = new byte[plain.Length];
        byte[] tag = new byte[16];
        using (var gcm = new AesGcm(_fileKey!))
        {
            gcm.Encrypt(nonce, plain, cipher, tag, Encoding.UTF8.GetBytes(associatedName.ToLowerInvariant()));
        }
        string content = FileMagic + "\n" + Convert.ToBase64String(nonce.Concat(cipher).Concat(tag).ToArray()) + "\n";

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, content, new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }

    private string Decrypt(string path, string associatedName)
    {
        string[] lines = File.ReadAllText(path).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length < 2 || lines[0] != FileMagic) throw new InvalidDataException("not a key chain file");
        byte[] all = Convert.FromBase64String(lines[1]);
        if (all.Length < 12 + 16) throw new InvalidDataException("too short");
        byte[] nonce = all[..12];
        byte[] tag = all[^16..];
        byte[] cipher = all[12..^16];
        byte[] plain = new byte[cipher.Length];
        using (var gcm = new AesGcm(_fileKey!))
        {
            gcm.Decrypt(nonce, cipher, tag, plain, Encoding.UTF8.GetBytes(associatedName.ToLowerInvariant())); // throws if altered
        }
        return Encoding.UTF8.GetString(plain);
    }

    // ---------------------------------------------------------------- helpers

    private static ServerHashStamp FromRecord(StampRecord r) => new()
    {
        Id = r.Id,
        StampTime = DateTime.SpecifyKind(DateTime.ParseExact(r.StampTime, TimeFormat, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal), DateTimeKind.Utc),
        KeyMode = r.KeyMode,
        KeyCheck = r.KeyCheck,
        WrappedKey = r.WrappedKey,
        PreviousHash = r.PreviousHash,
        StoredKey = string.Empty
    };

    private static ServerHashStamp Clone(ServerHashStamp s) => new()
    {
        Id = s.Id,
        StampTime = DateTime.SpecifyKind(s.StampTime, DateTimeKind.Utc),
        KeyMode = s.KeyMode,
        KeyCheck = s.KeyCheck,
        WrappedKey = s.WrappedKey,
        PreviousHash = s.PreviousHash,
        StoredKey = string.Empty
    };

    private void Sort() => _stamps!.Sort((a, b) => a.StampTime != b.StampTime ? a.StampTime.CompareTo(b.StampTime) : a.Id.CompareTo(b.Id));
}
