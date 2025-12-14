using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Common.Attributes;
using Common.Code;
using Common.Interfaces;
using Common.Models.Database;

using Microsoft.EntityFrameworkCore;

using MongoDB.Bson;

using ProcessorApplication.Database;
using ProcessorApplication.Database.Models;
using ProcessorApplication.Infrastructure;
using ProcessorApplication.Models;

using static ProcessorApplication.Services.HashStamps.HashBackupService;

namespace ProcessorApplication.Services.HashStamps;


public class HashStampService : IHashStampService
{
    private const int KeySizeInBytes = 32; //do not touch

    private readonly AppDbContext _context;
    private readonly ILogger<HashStampService> _logger;
    private readonly IHashBackupService _backupService;

    public HashStampService(
        AppDbContext context, 
        ILogger<HashStampService> logger,
        IHashBackupService backupService)
    {
        _context = context;
        _logger = logger;
        _backupService = backupService;
    }

    private static string ComputeSha256Hash(string rawData)
    {
        using (SHA256 sha256Hash = SHA256.Create())
        {
            byte[] bytes = sha256Hash.ComputeHash(Encoding.UTF8.GetBytes(rawData));

            // Convert byte array to a hex string (64 chars)
            var builder = new StringBuilder();
            for (int i = 0; i < bytes.Length; i++)
            {
                builder.Append(bytes[i].ToString("x2"));
            }
            return builder.ToString();
        }
    }

    private static string BytesToHexString(byte[] bytes)
    {
        var builder = new StringBuilder();
        foreach (byte b in bytes)
        {
            builder.Append(b.ToString("x2"));
        }
        return builder.ToString();
    }

    public async Task<ServerHashStamp> GenerateAndSaveNewStampAsync()
    {
        var previousStamp = await _context.HashStamps // Changed to ServerHashStamps to match DbContext
            .AsNoTracking() 
            .OrderByDescending(h => h.StampTime)
            .FirstOrDefaultAsync();

        byte[] keyBytes = new byte[KeySizeInBytes];
        using (var rng = RandomNumberGenerator.Create())
        {
            rng.GetBytes(keyBytes);
        }

        string newMasterKeyHex = BytesToHexString(keyBytes);

        string previousHashString = new string('0', 64);

        if (previousStamp != null)
        {
            previousHashString = ComputeSha256Hash(previousStamp.CalculateHashableContent() + newMasterKeyHex);
        }

        var newStamp = new ServerHashStamp
        {
            StampTime = DateTime.UtcNow,
            MasterKey = newMasterKeyHex, // Now using the 64-char hex string
            PreviousHash = previousHashString
        };

        _context.HashStamps.Add(newStamp);
        await _context.SaveChangesAsync();

        // Backup the new block (assuming interface accepts ServerHashStamp)
        _backupService.BackupServerBlock(newStamp);

        return newStamp;
    }

    public async Task<ServerHashStamp> GetLatestHashAsync()
    {
        var latestHash = await _context.HashStamps
            .AsNoTracking()
            .OrderByDescending(h => h.StampTime)
            .FirstOrDefaultAsync();

        if (latestHash == null || string.IsNullOrEmpty(latestHash.MasterKey))
        {
            _logger.LogWarning("No existing HashStamp found. Generating initial key.");
            var newStamp = await GenerateAndSaveNewStampAsync();
            return newStamp;
        }

        return latestHash;
    }

    public async Task<ServerHashStamp> GetHashByTimeAsync(DateTime time)
    {
        time = time.Kind != DateTimeKind.Utc ? time.ToUniversalTime() : time;

        var hashStamp = await _context.HashStamps
            .AsNoTracking()
            .Where(h => h.StampTime <= time)
            .OrderByDescending(h => h.StampTime)
            .FirstOrDefaultAsync();

        if (hashStamp != null && !string.IsNullOrEmpty(hashStamp.MasterKey))
        {
            return hashStamp;
        }

        var anyStampExists = await _context.HashStamps.AnyAsync();
        if (!anyStampExists)
        {
            _logger.LogWarning("Hash request for {Time} failed, but DB is empty. Generating Genesis.", time);
            var newStamp = await GenerateAndSaveNewStampAsync();
            return newStamp;
        }

        throw new InvalidOperationException($"No encryption key found for the specified time: {time.Date}. Requested time predates the Server Genesis Block.");
    }

    /// <summary>
    /// Verifies the entire HashStamp chain by recalculating hashes and returns a list of all corrupted records.
    /// </summary>
    public async Task<List<CorruptionReport>> VerifyChainIntegrityAsync()
    {
        _logger.LogInformation("Starting HashStamp chain integrity verification.");

        var corruptionReports = new List<CorruptionReport>();

        var chain = await _context.HashStamps
            .OrderBy(h => h.StampTime)
            .ToListAsync();

        if (chain.Count == 0)
        {
            _logger.LogInformation("Chain is empty (Genesis not created). Integrity check passed.");
            return corruptionReports;
        }

        ServerHashStamp previousStamp = null;

        for (int i = 0; i < chain.Count; i++)
        {
            ServerHashStamp currentStamp = chain[i];
            string currentChainIdentifier = $"HashStamp:{currentStamp.Id}";

            string expectedPreviousHash = "0"; // Genesis Hash

            if (previousStamp != null)
            {
                expectedPreviousHash = ComputeSha256Hash(previousStamp.CalculateHashableContent() + currentStamp.MasterKey);
            }

            if (currentStamp.PreviousHash != expectedPreviousHash)
            {
                _logger.LogError("Chain integrity compromised (LINK_BROKEN) at ID: {Id}.", currentStamp.Id);
                corruptionReports.Add(new CorruptionReport
                {
                    ChainIdentifier = currentChainIdentifier,
                    Reason = "LINK_BROKEN",
                    ExpectedHash = expectedPreviousHash,
                    StampTime = currentStamp.StampTime,
                    ActualHash = currentStamp.PreviousHash
                });
            }

            string calculatedCurrentHash = ComputeSha256Hash(currentStamp.CalculateHashableContent());
            HashBackupEntry backupEntry = _backupService.GetBackupServerBlock(currentStamp.Id);

            if (backupEntry != null)
            {
                string BackupEntryHash = ComputeSha256Hash(backupEntry.CalculateHashableContent());
                if (backupEntry.BlockHash != calculatedCurrentHash)
                {
                    _logger.LogCritical("Tampering detected: Offline BlockHash mismatch for Server Hash ID: {Id}.", currentStamp.Id);
                    corruptionReports.Add(new CorruptionReport
                    {
                        ChainIdentifier = currentChainIdentifier,
                        Reason = "CONTENT_MISMATCH_AGAINST_BACKUP",
                        ExpectedHash = backupEntry.BlockHash,
                        ActualHash = calculatedCurrentHash
                    });
                }

                if (backupEntry.PreviousBlockHash != currentStamp.PreviousHash)
                {
                    _logger.LogCritical("SEVERE TAMPERING DETECTED: Offline PreviousHash link mismatch for Server Hash ID: {Id}.", currentStamp.Id);
                    corruptionReports.Add(new CorruptionReport
                    {
                        ChainIdentifier = currentChainIdentifier,
                        Reason = "PREVIOUS_HASH_MISMATCH_AGAINST_BACKUP",
                        ExpectedHash = backupEntry.PreviousBlockHash,
                        ActualHash = currentStamp.PreviousHash
                    });
                }
            }

            previousStamp = currentStamp;
        }

        _logger.LogInformation("HashStamp chain integrity verification passed. Found {Count} issues.", corruptionReports.Count);
        return corruptionReports;
    }
}
