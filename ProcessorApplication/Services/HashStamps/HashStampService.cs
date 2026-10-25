using System.Security.Cryptography;
using System.Text;

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

    /// <summary>Recomputes the chain and compares it with the backups. Returns every problem found.</summary>
    Task<List<CorruptionReport>> VerifyChainIntegrityAsync();

    /// <summary>
    /// One-time conversion at startup: old plaintext keys are encrypted with the node root key,
    /// and every stamp is (re)written to the backups in the new, keyless format.
    /// </summary>
    Task ProtectLegacyKeysAsync();

    /// <summary>
    /// Re-inserts stamps that exist in the backups but are missing from the database, after checking
    /// that each recovered key matches its recorded KeyCheck. Returns how many were restored.
    /// </summary>
    Task<int> RestoreFromBackupAsync();
}

/// <summary>
/// Server key chain.
///
/// New stamps: key = HMAC(node root key, stamp time) - never stored. The database keeps the time, a hash of the key
/// (KeyCheck) and the link to the previous stamp. Old stamps (random keys created before this scheme) keep their key,
/// but only encrypted with the node root key. Callers still read <see cref="ServerHashStamp.MasterKey"/> as before.
///
/// Chain link: PreviousHash(n) = SHA256(content of stamp n-1). Content = time | mode | KeyCheck | PreviousHash.
/// Links made before this scheme use the old formula and are verified with it.
/// </summary>
public class HashStampService : IHashStampService
{
    public static readonly string GenesisHash = new('0', 64);

    private readonly AppDbContext _context;
    private readonly ILogger<HashStampService> _logger;
    private readonly IHashBackupService _backupService;
    private readonly INodeKeyService _nodeKey;

    public HashStampService(
        AppDbContext context,
        ILogger<HashStampService> logger,
        IHashBackupService backupService,
        INodeKeyService nodeKey)
    {
        _context = context;
        _logger = logger;
        _backupService = backupService;
        _nodeKey = nodeKey;
    }

    public static string ComputeSha256Hash(string rawData)
    {
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(rawData));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    // ---------------------------------------------------------------- keys

    /// <summary>Loads the root key; may create it only while nothing depends on it yet.</summary>
    private async Task EnsureRootKeyAsync()
    {
        bool keysDependOnRoot = await _context.HashStamps.AnyAsync(h => h.KeyMode != StampKeyMode.LegacyPlain);
        _nodeKey.EnsureLoaded(allowCreate: !keysDependOnRoot);
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

    public static string KeyCheckOf(string key) => ComputeSha256Hash("key-check|" + key);

    // ---------------------------------------------------------------- create / read

    public async Task<ServerHashStamp> GenerateAndSaveNewStampAsync()
    {
        await EnsureRootKeyAsync();

        var previous = await _context.HashStamps
            .AsNoTracking()
            .OrderByDescending(h => h.StampTime)
            .FirstOrDefaultAsync();

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

        _context.HashStamps.Add(stamp);
        await _context.SaveChangesAsync();

        _backupService.BackupServerBlock(stamp);

        stamp.MasterKey = key;
        return stamp;
    }

    public async Task<ServerHashStamp> GetLatestHashAsync()
    {
        var latest = await _context.HashStamps
            .AsNoTracking()
            .OrderByDescending(h => h.StampTime)
            .FirstOrDefaultAsync();

        if (latest == null)
        {
            _logger.LogWarning("No existing HashStamp found. Generating initial key.");
            return await GenerateAndSaveNewStampAsync();
        }

        await EnsureRootKeyAsync();
        return ResolveKey(latest);
    }

    public async Task<ServerHashStamp> GetHashByTimeAsync(DateTime time)
    {
        // Every time in the database is UTC, but SQLite hands it back as Unspecified. Treating Unspecified as
        // local shifted it by the machine's offset (UTC+2/+3) and could pick the previous day's stamp.
        time = time.Kind switch
        {
            DateTimeKind.Local => time.ToUniversalTime(),
            DateTimeKind.Unspecified => DateTime.SpecifyKind(time, DateTimeKind.Utc),
            _ => time
        };

        var stamp = await _context.HashStamps
            .AsNoTracking()
            .Where(h => h.StampTime <= time)
            .OrderByDescending(h => h.StampTime)
            .FirstOrDefaultAsync();

        if (stamp != null)
        {
            await EnsureRootKeyAsync();
            return ResolveKey(stamp);
        }

        if (!await _context.HashStamps.AnyAsync())
        {
            _logger.LogWarning("Hash request for {Time} failed, but DB is empty. Generating Genesis.", time);
            return await GenerateAndSaveNewStampAsync();
        }

        throw new InvalidOperationException($"No encryption key found for the specified time: {time:O}. Requested time predates the Server Genesis Block.");
    }

    // ---------------------------------------------------------------- startup conversion

    public async Task ProtectLegacyKeysAsync()
    {
        await EnsureRootKeyAsync();

        var chain = await _context.HashStamps.OrderBy(h => h.StampTime).ToListAsync();
        int converted = 0;

        foreach (var stamp in chain)
        {
            if (stamp.KeyMode == StampKeyMode.LegacyPlain)
            {
                string key = stamp.StoredKey;
                if (string.IsNullOrEmpty(key))
                {
                    _logger.LogError("Hash stamp {Id} has no key at all; leaving it untouched.", stamp.Id);
                    continue;
                }

                stamp.WrappedKey = _nodeKey.Wrap(key);
                stamp.KeyCheck = KeyCheckOf(key);
                stamp.KeyMode = StampKeyMode.LegacyWrapped;
                stamp.StoredKey = string.Empty;
                converted++;
            }
        }

        if (converted > 0)
        {
            await _context.SaveChangesAsync();
            _logger.LogWarning("Encrypted {Count} plaintext server key(s) with the node root key.", converted);

            // SQLite keeps old values in freed pages and in the WAL until they are rewritten: purge them from disk.
            try
            {
                await _context.Database.ExecuteSqlRawAsync("PRAGMA wal_checkpoint(TRUNCATE);");
                await _context.Database.ExecuteSqlRawAsync("VACUUM;");
                await _context.Database.ExecuteSqlRawAsync("PRAGMA wal_checkpoint(TRUNCATE);");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not compact the database after encrypting keys; old plaintext values may remain in free pages until it is compacted.");
            }
        }

        // Rewrite all backups in the keyless format (this also replaces old backup files that contained keys)
        foreach (var stamp in chain) _backupService.BackupServerBlock(stamp);
    }

    // ---------------------------------------------------------------- verification

    public async Task<List<CorruptionReport>> VerifyChainIntegrityAsync()
    {
        _logger.LogInformation("Starting HashStamp chain integrity verification.");
        var reports = new List<CorruptionReport>();

        var chain = await _context.HashStamps.AsNoTracking().OrderBy(h => h.StampTime).ToListAsync();
        if (chain.Count == 0) return reports;

        await EnsureRootKeyAsync();

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

            // 3. The backup agrees with the database
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
        _nodeKey.EnsureLoaded(allowCreate: false);

        var existing = await _context.HashStamps.AsNoTracking().ToListAsync();
        var known = existing.Select(s => s.Id).ToHashSet();
        var knownTimes = existing.Select(s => s.StampTime).ToHashSet();
        int restored = 0;

        foreach (var entry in _backupService.GetAllBackupEntries())
        {
            if (known.Contains(entry.Id)) continue;

            var stampTime = DateTime.SpecifyKind(
                DateTime.ParseExact(entry.StampTimeText, "yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AdjustToUniversal), DateTimeKind.Utc);
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

            _context.HashStamps.Add(stamp);
            restored++;
        }

        if (restored > 0) await _context.SaveChangesAsync();
        _logger.LogWarning("Restored {Count} hash stamp(s) from backup.", restored);
        return restored;
    }
}
