using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

using FileSharingModule.Configuration;
using FileSharingModule.Models;
using FileSharingModule.Models.Torrent;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using P2PModule.Interfaces;

namespace FileSharingModule.Services;


/// <summary>
/// Manages locally hosted, tracked, and seeded files.
/// Ingests new files, creates .ptorrent manifests, and controls swarm seeding.
/// </summary>
public interface IFileArchiveService
{
    /// <summary>
    /// Ingests a local file, generates its .ptorrent manifest (2-5 MB blocks, 64 KB slices),
    /// registers it with the local storage and swarm orchestrator, and saves metadata.
    /// </summary>
    Task<FileArchiveItemRecord> IngestAndTrackFileAsync(string sourceFilePath, bool isHidden = false);

    /// <summary>
    /// Retrieves all locally tracked files.
    /// </summary>
    Task<List<FileArchiveItemRecord>> GetTrackedFilesAsync(bool includeHidden = true);

    /// <summary>
    /// Retrieves a specific tracked file record by its ContentHash.
    /// </summary>
    Task<FileArchiveItemRecord?> GetTrackedFileAsync(string contentHash);

    /// <summary>
    /// Toggles the public visibility (IsHidden) of a tracked file.
    /// </summary>
    Task<bool> ToggleVisibilityAsync(string contentHash);

    /// <summary>
    /// Stops seeding and removes a file from local tracking.
    /// </summary>
    Task<bool> DeleteTrackedFileAsync(string contentHash, bool deletePhysicalFile = false);
}


public class FileArchiveService : IFileArchiveService
{
    private readonly IOptionsMonitor<FileSharingSettings> _settings;
    private readonly ISwarmOrchestratorService _swarmOrchestrator;
    private readonly INodeIdentityService _identity;
    private readonly ILogger<FileArchiveService> _logger;

    private readonly ConcurrentDictionary<string, FileArchiveItemRecord> _trackedItems = new();
    private readonly string _recordsIndexPath;

    public FileArchiveService(
        IOptionsMonitor<FileSharingSettings> settings,
        ISwarmOrchestratorService swarmOrchestrator,
        INodeIdentityService identity,
        ILogger<FileArchiveService> logger)
    {
        _settings = settings;
        _swarmOrchestrator = swarmOrchestrator;
        _identity = identity;
        _logger = logger;

        _recordsIndexPath = Path.Combine(_settings.CurrentValue.StorageRootPath, "archive_index.json");
        LoadIndexRecords();
    }

    public async Task<FileArchiveItemRecord> IngestAndTrackFileAsync(string sourceFilePath, bool isHidden = false)
    {
        if (!File.Exists(sourceFilePath))
            throw new FileNotFoundException("File not found for archive ingestion.", sourceFilePath);

        var localPeerDesc = new TorrentPeerDescriptor
        {
            NodeId = _identity.NodeId,
            SignalingEndpoints = _identity.GetEndpoints()
        };

        // 1. Generate clean PTorrentManifest (.ptorrent)
        var manifest = await PTorrentManifest.CreateFromFileAsync(
            sourceFilePath,
            new List<TorrentPeerDescriptor> { localPeerDesc },
            blockSizeBytes: _settings.CurrentValue.BlockSizeBytes,
            sliceSizeBytes: _settings.CurrentValue.SliceSizeBytes);

        // 2. Persist manifest to disk
        string manifestsDir = _settings.CurrentValue.ManifestsPath;
        Directory.CreateDirectory(manifestsDir);
        string manifestPath = Path.Combine(manifestsDir, $"{manifest.ContentHash}.ptorrent");
        await File.WriteAllTextAsync(manifestPath, manifest.SerializeToJson());

        // 3. Register tracked record (holds the local isHidden state)
        var record = new FileArchiveItemRecord
        {
            Manifest = manifest,
            LocalFilePath = sourceFilePath,
            ManifestFilePath = manifestPath,
            IsHidden = isHidden,
            RegisteredUtc = DateTime.UtcNow
        };

        _trackedItems[manifest.ContentHash] = record;
        await SaveIndexRecordsAsync();

        // 4. Start seeding in Swarm
        await _swarmOrchestrator.StartSeedingAsync(manifest, sourceFilePath);

        _logger.LogInformation("[FileArchive] Ingested and tracked file: {Name} (Hash: {Hash}, Hidden: {Hidden})",
            manifest.FileName, manifest.ContentHash, isHidden);
        return record;
    }

    public Task<List<FileArchiveItemRecord>> GetTrackedFilesAsync(bool includeHidden = true)
    {
        var list = _trackedItems.Values
            .Where(r => includeHidden || !r.IsHidden)
            .ToList();
        return Task.FromResult(list);
    }

    public Task<FileArchiveItemRecord?> GetTrackedFileAsync(string contentHash)
    {
        _trackedItems.TryGetValue(contentHash, out var record);
        return Task.FromResult(record);
    }

    public async Task<bool> ToggleVisibilityAsync(string contentHash)
    {
        if (_trackedItems.TryGetValue(contentHash, out var record))
        {
            record.IsHidden = !record.IsHidden;
            await SaveIndexRecordsAsync();
            _logger.LogInformation("[FileArchive] Toggled visibility for {Hash}. Hidden={Hidden}", contentHash, record.IsHidden);
            return true;
        }
        return false;
    }

    public async Task<bool> DeleteTrackedFileAsync(string contentHash, bool deletePhysicalFile = false)
    {
        if (_trackedItems.TryRemove(contentHash, out var record))
        {
            await _swarmOrchestrator.StopSeedingAsync(contentHash);

            if (File.Exists(record.ManifestFilePath))
            {
                try { File.Delete(record.ManifestFilePath); } catch { }
            }

            if (deletePhysicalFile && File.Exists(record.LocalFilePath))
            {
                try { File.Delete(record.LocalFilePath); } catch { }
            }

            await SaveIndexRecordsAsync();
            _logger.LogInformation("[FileArchive] Removed tracked file {Hash}.", contentHash);
            return true;
        }
        return false;
    }

    private void LoadIndexRecords()
    {
        try
        {
            if (File.Exists(_recordsIndexPath))
            {
                string json = File.ReadAllText(_recordsIndexPath);
                var items = JsonSerializer.Deserialize<List<FileArchiveItemRecord>>(json);
                if (items != null)
                {
                    foreach (var item in items)
                    {
                        if (File.Exists(item.LocalFilePath))
                        {
                            _trackedItems[item.Manifest.ContentHash] = item;
                            _ = _swarmOrchestrator.StartSeedingAsync(item.Manifest, item.LocalFilePath);
                        }
                    }
                }
                _logger.LogInformation("[FileArchive] Restored {Count} active seeding records from index.", _trackedItems.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[FileArchive] Error reading archive index from {Path}", _recordsIndexPath);
        }
    }

    private async Task SaveIndexRecordsAsync()
    {
        try
        {
            Directory.CreateDirectory(_settings.CurrentValue.StorageRootPath);
            string json = JsonSerializer.Serialize(_trackedItems.Values.ToList(), new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(_recordsIndexPath, json);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[FileArchive] Failed writing archive index to disk.");
        }
    }
}