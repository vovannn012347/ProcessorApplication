using System.Security.Cryptography;
using System.Text;

using System.Data.Common;
using System.Globalization;

using Microsoft.EntityFrameworkCore;

using ProcessorApplication.Database;
using ProcessorApplication.Database.Models;
using ProcessorApplication.Infrastructure;
using ProcessorApplication.Models;

namespace ProcessorApplication.Services.HashStamps;

public interface IHashStampService
{
    /// <summary>
    /// Creates the next stamp. Its key is derived from the node root key and the stamp time and is not stored.
    /// </summary>
    Task<ServerHashStamp> GenerateAndSaveNewStampAsync();

    /// <summary>The stamp (with its key filled in) that was active at <paramref name="time"/>.</summary>
    Task<ServerHashStamp> GetHashByTimeAsync(DateTime time);

    /// <summary>The newest stamp (with its key filled in); creates the first one if there is none.</summary>
    Task<ServerHashStamp> GetLatestHashAsync();

    /// <summary>A stamp by its id (with its key filled in), or null.</summary>
    Task<ServerHashStamp?> GetByIdAsync(int id);

    /// <summary>Recomputes the chain and compares it with the backups. Returns every problem found.</summary>
    Task<List<CorruptionReport>> VerifyChainIntegrityAsync();

    /// <summary>
    /// Startup: moves stamps still in the old database table into the key chain files (plaintext keys wrapped
    /// on the way) and rewrites every keyless backup.
    /// </summary>
    Task ProtectLegacyKeysAsync();

    /// <summary>
    /// Re-inserts stamps that exist in the backups but are missing from the key chain files, after checking
    /// that each recovered key matches its recorded KeyCheck. Returns how many were restored.
    /// </summary>
    Task<int> RestoreFromBackupAsync();
}

/// <summary>
/// Server key chain.
///
/// New stamps: key = HMAC(node root key, stamp time) - never stored. The chain itself lives in encrypted files in the
/// node home (<see cref="IHashStampStore"/>): time, a hash of the key (KeyCheck) and the link to the previous stamp
/// per file. Old stamps (random keys created before this scheme) keep their key, but only wrapped with the node root key.
/// Callers still read <see cref="ServerHashStamp.MasterKey"/> as before.
///
/// Chain link: PreviousHash(n) = SHA256(content of stamp n-1). Content = time | mode | KeyCheck | PreviousHash.
/// Links made before this scheme use the old formula and are verified with it.
///
/// Older versions kept the chain in the database table HashStamps. The migration MoveHashStampsToFiles renames it to
/// HashStamps_Legacy; on first use its rows are moved into the files and the table is dropped.
/// </summary>
public class HashStampService : IHashStampService
{
    public static readonly string GenesisHash = new('0', 64);
    public const string LegacyTable = "HashStamps_Legacy";

    // One move from the old table per process, before anything reads the chain.
    private static readonly SemaphoreSlim ReadyGate = new(1, 1);
    private static volatile bool _ready;

    private readonly AppDbContext _context;
    private readonly ILogger<HashStampService> _logger;
    private readonly IHashBackupService _backupService;
    private readonly INodeKeyService _nodeKey;
    private readonly IHashStampStore _store;

    public HashStampService(
        AppDbContext context,
        ILogger<HashStampService> logger,
        IHashBackupService backupService,
        INodeKeyService nodeKey,
        IHashStampStore store)
    {
        _context = context;
        _logger = logger;
        _backupService = backupService;
        _nodeKey = nodeKey;
        _store = store;
    }

    public static string ComputeSha256Hash(string rawData)
    {
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(rawData));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public static string KeyCheckOf(string key) => ComputeSha256Hash("key-check|" + key);

    // ---------------------------------------------------------------- startup / old table

    /// <summary>Root key loaded (created only while nothing depends on it) and the old table moved into the files.</summary>
    private async Task EnsureReadyAsync()
    {
        if (_ready) return;
        await ReadyGate.WaitAsync();
        try
        {
            if (_ready) return;

            var legacy = await ReadLegacyTableAsync(); // null: no old table
            bool keysDependOnRoot = _store.HasFiles || (legacy?.Any(s => s.KeyMode != StampKeyMode.LegacyPlain) ?? false);
            _nodeKey.EnsureLoaded(allowCreate: !keysDependOnRoot);

            // Also an empty old table (a fresh database runs every migration) is removed.
            if (legacy != null) await MoveLegacyStampsAsync(legacy);
            _ready = true;
        }
        finally
        {
            ReadyGate.Release();
        }
    }

    private async Task<List<ServerHashStamp>?> ReadLegacyTableAsync()
    {
        var result = new List<ServerHashStamp>();
        DbConnection conn = _context.Database.GetDbConnection();
        bool opened = false;
        try
        {
            if (conn.State != System.Data.ConnectionState.Open) { await conn.OpenAsync(); opened = true; }

            using (var check = conn.CreateCommand())
            {
                check.CommandText = "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = $name";
                var p = check.CreateParameter(); p.ParameterName = "$name"; p.Value = LegacyTable; check.Parameters.Add(p);
                if (Convert.ToInt64(await check.ExecuteScalarAsync()) == 0) return null;
            }

            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT Id, StampTime, MasterKey, PreviousHash, KeyMode, WrappedKey, KeyCheck FROM {LegacyTable} ORDER BY StampTime";
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                result.Add(new ServerHashStamp
                {
                    Id = reader.GetInt32(0),
                    StampTime = DateTime.SpecifyKind(DateTime.Parse(reader.GetString(1), CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal), DateTimeKind.Utc),
                    StoredKey = reader.IsDBNull(2) ? "" : reader.GetString(2),
                    PreviousHash = reader.IsDBNull(3) ? "" : reader.GetString(3),
                    KeyMode = (StampKeyMode)reader.GetInt32(4),
                    WrappedKey = reader.IsDBNull(5) ? null : reader.GetString(5),
                    KeyCheck = reader.IsDBNull(6) ? null : reader.GetString(6)
                });
            }
        }
        finally
        {
            if (opened) await conn.CloseAsync();
        }
        return result;
    }

    private async Task MoveLegacyStampsAsync(List<ServerHashStamp> legacy)
    {
        int moved = 0, skipped = 0;
        foreach (var stamp in legacy)
        {
            if (stamp.KeyMode == StampKeyMode.LegacyPlain)
            {
                if (string.IsNullOrEmpty(stamp.StoredKey))
                {
                    _logger.LogError("[Key chain] Old stamp {Id} has no key at all; not moved.", stamp.Id);
                    skipped++;
                    continue;
                }
                stamp.WrappedKey = _nodeKey.Wrap(stamp.StoredKey);
                stamp.KeyCheck = KeyCheckOf(stamp.StoredKey);
                stamp.KeyMode = StampKeyMode.LegacyWrapped;
                stamp.StoredKey = string.Empty;
            }

            if (_store.GetById(stamp.Id) == null)
            {
                _store.Put(stamp);
                moved++;
            }
            _backupService.BackupServerBlock(stamp);
        }

        // Drop the old table only when every stamp it held is in the files.
        var inFiles = _store.GetAll().Select(s => s.Id).ToHashSet();
        if (skipped == 0 && legacy.All(s => inFiles.Contains(s.Id)))
        {
            await _context.Database.ExecuteSqlRawAsync($"DROP TABLE {LegacyTable};");
            try
            {
                // SQLite keeps old values in freed pages and in the WAL until they are rewritten: purge them from disk.
                await _context.Database.ExecuteSqlRawAsync("PRAGMA wal_checkpoint(TRUNCATE);");
                await _context.Database.ExecuteSqlRawAsync("VACUUM;");
                await _context.Database.ExecuteSqlRawAsync("PRAGMA wal_checkpoint(TRUNCATE);");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Key chain] Could not compact the database after removing the old stamp table.");
            }
            if (legacy.Count > 0)
                _logger.LogWarning("[Key chain] Moved {Count} stamp(s) from the database into {Folder}; the old table is removed.", moved, _store.Folder);
        }
        else
        {
            _logger.LogError("[Key chain] {Skipped} old stamp(s) could not be moved; the table {Table} is kept.", skipped, LegacyTable);
        }
    }

    /// <summary>Fills <see cref="ServerHashStamp.MasterKey"/> from wherever this stamp's key lives.</summary>
    private ServerHashStamp ResolveKey(ServerHashStamp stamp)
    {
        stamp.MasterKey = stamp.KeyMode switch
        {
            StampKeyMode.Derived => _nodeKey.DerivePeriodKey(stamp.StampTime),
            StampKeyMode.LegacyWrapped => _nodeKey.Unwrap(stamp.WrappedKey ?? throw new CryptographicException($"Stamp {stamp.Id} has no wrapped key.")),
            _ => stamp.StoredKey
        };
        return stamp;
    }

    private static DateTime AsUtc(DateTime time) => time.Kind switch
    {
        // SQLite hands times back as Unspecified; they are UTC. Treating them as local shifted them by the
        // machine's offset (UTC+2/+3) and could pick the previous period's stamp.
        DateTimeKind.Local => time.ToUniversalTime(),
        DateTimeKind.Unspecified => DateTime.SpecifyKind(time, DateTimeKind.Utc),
        _ => time
    };

    // ---------------------------------------------------------------- create / read

    public async Task<ServerHashStamp> GenerateAndSaveNewStampAsync()
    {
        await EnsureReadyAsync();
        return CreateStamp();
    }

    private ServerHashStamp CreateStamp()
    {
        var previous = _store.GetLatest();

        // Whole milliseconds: the time is the derivation input and must survive every storage format unchanged.
        var now = DateTime.UtcNow;
        var stampTime = new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerMillisecond, DateTimeKind.Utc);
        if (previous != null && stampTime <= previous.StampTime) stampTime = previous.StampTime.AddMilliseconds(1);

        string key = _nodeKey.DerivePeriodKey(stampTime);

        var stamp = new ServerHashStamp
        {
            StampTime = stampTime,
            KeyMode = StampKeyMode.Derived,
            StoredKey = string.Empty,
            KeyCheck = KeyCheckOf(key),
            PreviousHash = previous == null ? GenesisHash : ComputeSha256Hash(previous.CalculateBlockContent())
        };

        _store.Append(stamp);
        _backupService.BackupServerBlock(stamp);

        stamp.MasterKey = key;
        return stamp;
    }

    public async Task<ServerHashStamp> GetLatestHashAsync()
    {
        await EnsureReadyAsync();
        var latest = _store.GetLatest();
        if (latest == null)
        {
            _logger.LogWarning("No existing HashStamp found. Generating initial key.");
            return CreateStamp();
        }
        return ResolveKey(latest);
    }

    public async Task<ServerHashStamp?> GetByIdAsync(int id)
    {
        await EnsureReadyAsync();
        var stamp = _store.GetById(id);
        return stamp == null ? null : ResolveKey(stamp);
    }

    public async Task<ServerHashStamp> GetHashByTimeAsync(DateTime time)
    {
        await EnsureReadyAsync();
        time = AsUtc(time);

        var stamp = _store.GetAt(time);
        if (stamp != null) return ResolveKey(stamp);

        if (_store.GetLatest() == null)
        {
            _logger.LogWarning("Hash request for {Time} failed, but the key chain is empty. Generating Genesis.", time);
            return CreateStamp();
        }

        throw new InvalidOperationException($"No encryption key found for the specified time: {time:O}. Requested time predates the Server Genesis Block.");
    }

    // ---------------------------------------------------------------- startup conversion

    public async Task ProtectLegacyKeysAsync()
    {
        await EnsureReadyAsync();
        // Keyless backups of every stamp (also replaces old backup files that contained keys)
        foreach (var stamp in _store.GetAll()) _backupService.BackupServerBlock(stamp);
    }

    // ---------------------------------------------------------------- verification

    public async Task<List<CorruptionReport>> VerifyChainIntegrityAsync()
    {
        _logger.LogInformation("Starting HashStamp chain integrity verification.");
        await EnsureReadyAsync();
        var reports = new List<CorruptionReport>();

        foreach (var problem in _store.Problems)
        {
            reports.Add(new CorruptionReport { ChainIdentifier = "KeyChain:" + problem.File, Reason = problem.Reason, ActualHash = problem.Detail });
        }

        var chain = _store.GetAll().ToList();
        var inChain = chain.Select(s => s.Id).ToHashSet();

        // Stamps the backups know but the chain files don't (a removed file anywhere in the chain)
        foreach (var backup in _backupService.GetAllBackupEntries())
        {
            if (!inChain.Contains(backup.Id))
                reports.Add(new CorruptionReport { ChainIdentifier = $"HashStamp:{backup.Id}", Reason = "STAMP_FILE_MISSING", StampTime = backup.StampTime,
                    ExpectedHash = backup.BlockHash });
        }

        ServerHashStamp? previous = null;
        foreach (var current in chain)
        {
            string id = $"HashStamp:{current.Id}";

            // 1. The key can be recovered and matches its recorded check
            try
            {
                ResolveKey(current);
                if (current.KeyCheck != null && KeyCheckOf(current.MasterKey) != current.KeyCheck)
                {
                    reports.Add(new CorruptionReport { ChainIdentifier = id, Reason = "KEY_CHECK_MISMATCH", StampTime = current.StampTime,
                        ExpectedHash = current.KeyCheck, ActualHash = KeyCheckOf(current.MasterKey) });
                }
            }
            catch (CryptographicException ex)
            {
                reports.Add(new CorruptionReport { ChainIdentifier = id, Reason = "KEY_UNRECOVERABLE", StampTime = current.StampTime, ActualHash = ex.Message });
            }

            // 2. The link to the previous stamp
            string expectedPrevious;
            if (previous == null)
                expectedPrevious = GenesisHash;
            else if (current.KeyMode == StampKeyMode.Derived)
                expectedPrevious = ComputeSha256Hash(previous.CalculateBlockContent());
            else
                expectedPrevious = ComputeSha256Hash(previous.CalculateHashableContent() + current.MasterKey); // legacy formula

            bool genesisOk = previous == null && (current.PreviousHash == GenesisHash || current.PreviousHash == "0");
            if (!genesisOk && current.PreviousHash != expectedPrevious)
            {
                reports.Add(new CorruptionReport { ChainIdentifier = id, Reason = "LINK_BROKEN", StampTime = current.StampTime,
                    ExpectedHash = expectedPrevious, ActualHash = current.PreviousHash });
            }

            // 3. The backup agrees with the chain files
            var backup = _backupService.GetBackupServerBlock(current.Id);
            if (backup == null)
            {
                reports.Add(new CorruptionReport { ChainIdentifier = id, Reason = "BACKUP_MISSING", StampTime = current.StampTime });
            }
            else
            {
                string blockHash = ComputeSha256Hash(current.CalculateBlockContent());
                if (backup.BlockHash != blockHash)
                    reports.Add(new CorruptionReport { ChainIdentifier = id, Reason = "CONTENT_MISMATCH_AGAINST_BACKUP", StampTime = current.StampTime,
                        ExpectedHash = backup.BlockHash, ActualHash = blockHash });
                if (backup.PreviousBlockHash != current.PreviousHash)
                    reports.Add(new CorruptionReport { ChainIdentifier = id, Reason = "PREVIOUS_HASH_MISMATCH_AGAINST_BACKUP", StampTime = current.StampTime,
                        ExpectedHash = backup.PreviousBlockHash, ActualHash = current.PreviousHash });
            }

            previous = current;
        }

        _logger.LogInformation("HashStamp chain verification finished with {Count} issue(s).", reports.Count);
        return reports;
    }

    // ---------------------------------------------------------------- recovery

    public async Task<int> RestoreFromBackupAsync()
    {
        await EnsureReadyAsync();

        var existing = _store.GetAll();
        var known = existing.Select(s => s.Id).ToHashSet();
        var knownTimes = existing.Select(s => s.StampTime).ToHashSet();
        int restored = 0;

        foreach (var entry in _backupService.GetAllBackupEntries())
        {
            if (known.Contains(entry.Id)) continue;
            if (string.IsNullOrEmpty(entry.StampTimeText)) continue; // old backup format: no exact time, cannot be verified

            var stampTime = DateTime.SpecifyKind(
                DateTime.ParseExact(entry.StampTimeText, "yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal), DateTimeKind.Utc);
            if (knownTimes.Contains(stampTime)) continue;

            var stamp = new ServerHashStamp
            {
                Id = entry.Id,
                StampTime = stampTime,
                KeyMode = entry.KeyMode,
                KeyCheck = entry.KeyCheck,
                WrappedKey = entry.WrappedKey,
                StoredKey = string.Empty,
                PreviousHash = entry.PreviousBlockHash
            };

            // Only restore what the root key can actually reproduce
            string key = ResolveKey(stamp).MasterKey;
            if (KeyCheckOf(key) != entry.KeyCheck)
            {
                _logger.LogError("Backup of stamp {Id} does not match this node's root key; skipped.", entry.Id);
                continue;
            }
            if (ComputeSha256Hash(stamp.CalculateBlockContent()) != entry.BlockHash)
            {
                _logger.LogError("Backup of stamp {Id} is internally inconsistent; skipped.", entry.Id);
                continue;
            }

            _store.Put(stamp);
            restored++;
        }

        _logger.LogWarning("Restored {Count} hash stamp(s) from backup.", restored);
        return restored;
    }
}
