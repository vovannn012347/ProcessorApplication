using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Common.Attributes;
using Common.Code;
using Common.Interfaces;
using Common.Models.Database;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using MongoDB.Bson;

using ProcessorApplication.Configuration.Settings;
using ProcessorApplication.Database;
using ProcessorApplication.Database.Models;
using ProcessorApplication.Infrastructure;
using ProcessorApplication.Models;

namespace ProcessorApplication.Services.HashStamps;
public class HashBackupService : IHashBackupService
{
    private readonly ILogger<HashBackupService> _logger;
    private readonly IOptionsMonitor<SecuritySettings> _settingsMonitor;

    private static readonly JsonSerializerOptions JsonWriteOptions = new JsonSerializerOptions
    {
        WriteIndented = true
    };

    public HashBackupService
        (ILogger<HashBackupService> logger,
        IOptionsMonitor<SecuritySettings> settingsMonitor)
    {
        _logger = logger;
        _settingsMonitor = settingsMonitor;
        //_logger.LogInformation("Hash Backup Service Initialized (Simulated Disk/External Storage).");
    }

    private string GetFilePath(int stampId)
    {
        // Get the current path from the live configuration monitor
        string basePath = _settingsMonitor.CurrentValue.HashStampBackupFilePath;

        // Ensure the base directory exists before writing
        Directory.CreateDirectory(basePath);

        // File naming convention: [StampId].json
        return Path.Combine(basePath, $"{stampId}.json");
    }

    /// <summary>
    /// Saves the hash of a newly created ServerHashStamp block to an external disk.
    /// </summary>
    /// <param name="backupEntry">hash record.</param>
    public void BackupServerBlock(ServerHashStamp stamp)
    {
        try
        {
            // Create the immutable forensic entry from the ServerHashStamp model
            var entry = new HashBackupEntry
            {
                Id = stamp.Id,
                StampTime = stamp.StampTime,
                // Note: Since ID is not included in CalculateHashableContent(), we must calculate the hash separately
                // to include in the backup.
                BlockHash = stamp.MasterKey,
                PreviousBlockHash = stamp.PreviousHash
            };

            string filePath = GetFilePath(stamp.Id);
            string jsonString = JsonSerializer.Serialize(entry, JsonWriteOptions);

            // Writing the file using UTF8 encoding for standard forensic readability
            File.WriteAllText(filePath, jsonString, Encoding.UTF8);

            _logger.LogInformation("Successfully backed up Server Hash ID {Id} to file: {Path}", stamp.Id, filePath);
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "FATAL ERROR: Failed to write forensic backup file for Stamp ID {Id}.", stamp.Id);
            // In a production system, this failure should trigger alerts.
        }
    }

    public HashBackupEntry GetBackupServerBlock(int id)
    {
        try
        {
            string filePath = GetFilePath(id);

            if (!File.Exists(filePath))
            {
                _logger.LogWarning("Forensic backup file not found for ID {Id} at path: {Path}", id, filePath);
                return null;
            }

            string jsonString = File.ReadAllText(filePath, Encoding.UTF8);

            return JsonSerializer.Deserialize<HashBackupEntry>(jsonString);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read or parse forensic backup file for ID {Id}.", id);
            return null;
        }
    }
}