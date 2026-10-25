using System.Security.Cryptography;
using System.Text;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Newtonsoft.Json;

using ProcessingModule.Configuration;
using ProcessingModule.Database;
using ProcessingModule.Database.Models;
using ProcessingModule.Infrastructure;
using ProcessingModule.Models;

using ProcessorApplication.Services.HashStamps;

namespace ProcessingModule.Services.Processing;

public sealed class ChainProblem
{
    public long Sequence { get; init; }
    public Guid? StepId { get; init; }
    public string Reason { get; init; } = "";
    public string Detail { get; init; } = "";
}

public sealed class ChainVerification
{
    public string OwnerId { get; init; } = "";
    public long Entries { get; set; }
    public bool Ok => Problems.Count == 0;
    public List<ChainProblem> Problems { get; } = new();
}

public interface IResultChainService
{
    /// <summary>Appends the finished step to its owner's chain and returns the entry (null if the step can't be chained).</summary>
    Task<ResultChainEntry?> AppendAsync(Guid stepId, CancellationToken ct = default);

    /// <summary>
    /// Checks an owner's chain: seals, links, content, sequence, head backup; with <paramref name="checkFiles"/>
    /// also re-hashes the result files still on disk.
    /// </summary>
    Task<ChainVerification> VerifyAsync(string ownerId, bool checkFiles = false, CancellationToken ct = default);
}

/// <summary>
/// Per-user hash chain over processing results. See <see cref="ResultChainEntry"/> for what is protected and how.
/// Seal key: <see cref="IUserSealingKeyProvider"/> (owner's personal key + server key of the period; nothing stored).
/// </summary>
public sealed class ResultChainService : IResultChainService
{
    public const string Purpose = "processing-results";
    private const string Genesis = "0000000000000000000000000000000000000000000000000000000000000000";

    // One writer at a time: sequence numbers and links must not interleave
    private static readonly SemaphoreSlim AppendLock = new(1, 1);

    private readonly ProcessorDbContext _db;
    private readonly IUserSealingKeyProvider _keys;
    private readonly IOptionsMonitor<ProcessorSettings> _settings;
    private readonly ILogger<ResultChainService> _logger;

    public ResultChainService(ProcessorDbContext db, IUserSealingKeyProvider keys,
        IOptionsMonitor<ProcessorSettings> settings, ILogger<ResultChainService> logger)
    {
        _db = db;
        _keys = keys;
        _settings = settings;
        _logger = logger;
    }

    private sealed class FileHash
    {
        [JsonProperty("path")] public string Path { get; set; } = "";
        [JsonProperty("size")] public long Size { get; set; }
        [JsonProperty("sha256")] public string Sha256 { get; set; } = "";
    }

    private sealed class Payload
    {
        [JsonProperty("inputs")] public List<FileHash> Inputs { get; set; } = new();
        [JsonProperty("outputs")] public List<FileHash> Outputs { get; set; } = new();
    }

    private sealed class Head
    {
        public string OwnerId { get; set; } = "";
        public long Sequence { get; set; }
        public string SealHash { get; set; } = "";
        public DateTime Updated { get; set; }
    }

    // ------------------------------------------------------------------ append

    public async Task<ResultChainEntry?> AppendAsync(Guid stepId, CancellationToken ct = default)
    {
        var step = await _db.ProcessingJobs.Include(s => s.ParentJob).FirstOrDefaultAsync(s => s.Id == stepId, ct);
        if (step == null) return null;

        var existing = await _db.ResultChain.FirstOrDefaultAsync(e => e.StepId == stepId, ct);
        if (existing != null) return existing; // already chained (e.g. retried save)

        string jobRoot = Path.GetFullPath(step.ParentJob.PhysicalPathRoot);
        var payload = BuildPayload(step, jobRoot);
        string payloadJson = JsonConvert.SerializeObject(payload, Formatting.None);

        string? artifactHash = step.ScriptIndexId.HasValue
            ? await _db.Scripts.Where(s => s.Id == step.ScriptIndexId).Select(s => s.ArtifactHash).FirstOrDefaultAsync(ct)
            : null;

        await AppendLock.WaitAsync(ct);
        try
        {
            string owner = step.ParentJob.InitiatorUserId ?? "system";
            var last = await _db.ResultChain
                .Where(e => e.OwnerId == owner)
                .OrderByDescending(e => e.Sequence)
                .FirstOrDefaultAsync(ct);

            var now = DateTime.UtcNow;
            var entry = new ResultChainEntry
            {
                OwnerId = owner,
                Sequence = (last?.Sequence ?? 0) + 1,
                CreatedTime = new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerMillisecond, DateTimeKind.Utc),
                JobId = step.ParentJobId,
                StepId = step.Id,
                ScriptId = step.ScriptId,
                ScriptVersion = step.ScriptVersion,
                ScriptBuildId = step.ScriptIndexId,
                ArtifactHash = artifactHash,
                Status = step.Status,
                PayloadJson = payloadJson
            };
            entry.ContentHash = ComputeContentHash(entry);

            string previousHash = Genesis;
            if (last != null)
            {
                var lastKey = await _keys.GetSealingKeyForStampAsync(owner, Purpose, last.StampId, ct);
                previousHash = OpenSeal(last, lastKey.Key).EntryHash; // also proves the tail wasn't tampered with
            }

            var key = await _keys.GetSealingKeyAsync(owner, Purpose, entry.CreatedTime, ct);
            entry.StampId = key.StampId;
            entry.UserBound = key.IsUserBound;
            string entryHash = ComputeEntryHash(entry.ContentHash, previousHash);
            entry.Seal = MakeSeal(entry, key.Key, entryHash, previousHash);

            _db.ResultChain.Add(entry);
            step.ResultHash = entry.ContentHash;
            await _db.SaveChangesAsync(ct);

            WriteHead(entry);
            return entry;
        }
        finally
        {
            AppendLock.Release();
        }
    }

    // ------------------------------------------------------------------ verify

    public async Task<ChainVerification> VerifyAsync(string ownerId, bool checkFiles = false, CancellationToken ct = default)
    {
        var result = new ChainVerification { OwnerId = ownerId };
        var entries = await _db.ResultChain.AsNoTracking()
            .Where(e => e.OwnerId == ownerId)
            .OrderBy(e => e.Sequence)
            .ToListAsync(ct);
        result.Entries = entries.Count;

        string previousHash = Genesis;
        long expectedSequence = 1;

        foreach (var entry in entries)
        {
            void Problem(string reason, string detail = "") =>
                result.Problems.Add(new ChainProblem { Sequence = entry.Sequence, StepId = entry.StepId, Reason = reason, Detail = detail });

            if (entry.Sequence != expectedSequence)
            {
                Problem("SEQUENCE_GAP", $"expected {expectedSequence}");
                expectedSequence = entry.Sequence;
            }
            expectedSequence++;

            string recomputed = ComputeContentHash(entry);
            if (recomputed != entry.ContentHash) Problem("CONTENT_HASH_MISMATCH", "stored hash doesn't match the stored record");

            SealingKey key;
            try
            {
                key = await _keys.GetSealingKeyForStampAsync(ownerId, Purpose, entry.StampId, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Wrong/missing node root key, missing server stamp: the seal can't be checked at all
                Problem("KEY_UNAVAILABLE", ex.Message);
                previousHash = "?";
                continue;
            }
            if (entry.UserBound && !key.IsUserBound)
            {
                Problem("OWNER_KEY_UNAVAILABLE", "the entry was sealed with the user's personal key, but the user (or their key) is gone");
                previousHash = "?";
                continue;
            }

            (string EntryHash, string PreviousHash) opened;
            try
            {
                opened = OpenSeal(entry, key.Key);
            }
            catch (CryptographicException ex)
            {
                Problem("SEAL_INVALID", ex.Message);
                previousHash = "?";
                continue;
            }

            if (opened.EntryHash != ComputeEntryHash(recomputed, opened.PreviousHash))
                Problem("RECORD_ALTERED", "the record no longer matches its seal");
            if (previousHash != "?" && opened.PreviousHash != previousHash)
                Problem("LINK_BROKEN", "the previous entry was changed, removed or reordered");

            if (checkFiles) CheckFiles(entry, Problem);

            previousHash = opened.EntryHash;
        }

        // Tail: the head backup lives outside the database
        var head = ReadHead(ownerId);
        var tail = entries.LastOrDefault();
        if (head != null)
        {
            if (tail == null || tail.Sequence < head.Sequence)
                result.Problems.Add(new ChainProblem { Sequence = tail?.Sequence ?? 0, Reason = "TAIL_MISSING",
                    Detail = $"head backup says the chain reaches {head.Sequence}" });
            else if (tail.Sequence == head.Sequence && Sha256Hex(tail.Seal) != head.SealHash)
                result.Problems.Add(new ChainProblem { Sequence = tail.Sequence, StepId = tail.StepId, Reason = "TAIL_REPLACED",
                    Detail = "the last entry differs from the head backup" });
        }
        else if (tail != null)
        {
            result.Problems.Add(new ChainProblem { Sequence = tail.Sequence, Reason = "HEAD_BACKUP_MISSING",
                Detail = "can't confirm that nothing was removed from the end" });
        }

        return result;
    }

    private void CheckFiles(ResultChainEntry entry, Action<string, string> problem)
    {
        var job = _db.Jobs.AsNoTracking().FirstOrDefault(j => j.Id == entry.JobId);
        if (job == null || !Directory.Exists(job.PhysicalPathRoot)) return; // purged: nothing to compare
        string root = Path.GetFullPath(job.PhysicalPathRoot);

        var payload = JsonConvert.DeserializeObject<Payload>(entry.PayloadJson) ?? new Payload();
        foreach (var f in payload.Inputs.Concat(payload.Outputs))
        {
            string full = Path.Combine(root, f.Path.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(full)) { problem("FILE_MISSING", f.Path); continue; }
            if (HashFile(full) != f.Sha256) problem("FILE_CHANGED", f.Path);
        }
    }

    // ------------------------------------------------------------------ hashing

    private Payload BuildPayload(OrchestratedTaskSubJob step, string jobRoot)
    {
        var payload = new Payload();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddFile(List<FileHash> list, string full)
        {
            string rel = Path.GetRelativePath(jobRoot, full).Replace('\\', '/');
            if (rel.StartsWith("..") || !seen.Add(rel)) return;
            list.Add(new FileHash { Path = rel, Size = new FileInfo(full).Length, Sha256 = HashFile(full) });
        }

        void AddPath(List<FileHash> list, string full)
        {
            if (File.Exists(full)) AddFile(list, full);
            else if (Directory.Exists(full))
                foreach (var f in Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories)) AddFile(list, f);
        }

        string stepDir = Path.Combine(jobRoot, step.StepDirectoryName);

        // Inputs: what the processing manifest points the script at (shared uploads, direct_input.json)
        string manifestPath = Path.Combine(jobRoot, MicsConstants.OrchestrationDirectory, $"{step.Id}.json");
        if (File.Exists(manifestPath))
        {
            var manifest = JsonConvert.DeserializeObject<ProcessingManifest>(File.ReadAllText(manifestPath));
            foreach (var rel in (manifest?.InputsFoldersBase ?? new()).Values.Distinct())
            {
                string full = Path.GetFullPath(Path.Combine(jobRoot, rel));
                if (full.StartsWith(jobRoot, StringComparison.OrdinalIgnoreCase)) AddPath(payload.Inputs, full);
            }
        }

        // Outputs: everything left in the step folder after unmapped files were purged
        if (Directory.Exists(stepDir))
        {
            foreach (var f in Directory.EnumerateFiles(stepDir, "*", SearchOption.AllDirectories)) AddFile(payload.Outputs, f);
        }

        payload.Inputs = payload.Inputs.OrderBy(f => f.Path, StringComparer.Ordinal).ToList();
        payload.Outputs = payload.Outputs.OrderBy(f => f.Path, StringComparer.Ordinal).ToList();
        return payload;
    }

    private static string HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan);
        using var sha = SHA256.Create(); // SHA256.HashData(Stream) needs .NET 7
        return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
    }

    private static string Sha256Hex(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();

    public static string ComputeContentHash(ResultChainEntry e) => Sha256Hex(string.Join("\n",
        "result-chain/v1",
        e.OwnerId,
        e.Sequence.ToString(System.Globalization.CultureInfo.InvariantCulture),
        DateTime.SpecifyKind(e.CreatedTime, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture),
        e.JobId.ToString("D"),
        e.StepId.ToString("D"),
        e.ScriptId,
        e.ScriptVersion ?? "",
        e.ScriptBuildId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "",
        e.ArtifactHash ?? "",
        e.Status,
        e.PayloadJson));

    private static string ComputeEntryHash(string contentHash, string previousHash) => Sha256Hex($"{contentHash}|{previousHash}");

    // ------------------------------------------------------------------ seal (AES-GCM)

    private static byte[] Aad(ResultChainEntry e) =>
        Encoding.UTF8.GetBytes($"{e.OwnerId}|{e.Sequence}|{e.StepId:D}|{e.StampId}");

    private static string MakeSeal(ResultChainEntry e, byte[] key, string entryHash, string previousHash)
    {
        byte[] plain = Convert.FromHexString(entryHash).Concat(Convert.FromHexString(previousHash)).ToArray();
        byte[] nonce = RandomNumberGenerator.GetBytes(12);
        byte[] cipher = new byte[plain.Length];
        byte[] tag = new byte[16];
        using (var gcm = new AesGcm(key))
        {
            gcm.Encrypt(nonce, plain, cipher, tag, Aad(e));
        }
        return Convert.ToBase64String(nonce.Concat(cipher).Concat(tag).ToArray());
    }

    private static (string EntryHash, string PreviousHash) OpenSeal(ResultChainEntry e, byte[] key)
    {
        byte[] all;
        try { all = Convert.FromBase64String(e.Seal); }
        catch (FormatException) { throw new CryptographicException("Seal is not valid base64."); }
        if (all.Length != 12 + 64 + 16) throw new CryptographicException("Seal has the wrong length.");

        byte[] plain = new byte[64];
        using (var gcm = new AesGcm(key))
        {
            // Fails if the seal, its position (owner/sequence/step/stamp) or the key is wrong
            gcm.Decrypt(all[..12], all[12..76], all[76..], plain, Aad(e));
        }
        return (Convert.ToHexString(plain[..32]).ToLowerInvariant(), Convert.ToHexString(plain[32..]).ToLowerInvariant());
    }

    // ------------------------------------------------------------------ head backup

    private string HeadDirectory
    {
        get
        {
            string configured = _settings.CurrentValue.ResultChainBackupPath;
            string path = string.IsNullOrWhiteSpace(configured) ? "Module_Data/Processor/result_chain" : configured;
            return Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(AppContext.BaseDirectory, path));
        }
    }

    private string HeadPath(string ownerId) => Path.Combine(HeadDirectory, $"{Sha256Hex("owner|" + ownerId)[..24]}.head.json");

    private void WriteHead(ResultChainEntry entry)
    {
        try
        {
            Directory.CreateDirectory(HeadDirectory);
            var head = new Head { OwnerId = entry.OwnerId, Sequence = entry.Sequence, SealHash = Sha256Hex(entry.Seal), Updated = DateTime.UtcNow };
            string path = HeadPath(entry.OwnerId);
            File.WriteAllText(path + ".tmp", JsonConvert.SerializeObject(head, Formatting.Indented));
            File.Move(path + ".tmp", path, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not write the result chain head backup for {Owner}", entry.OwnerId);
        }
    }

    private Head? ReadHead(string ownerId)
    {
        try
        {
            string path = HeadPath(ownerId);
            return File.Exists(path) ? JsonConvert.DeserializeObject<Head>(File.ReadAllText(path)) : null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not read the result chain head backup for {Owner}", ownerId);
            return null;
        }
    }
}
