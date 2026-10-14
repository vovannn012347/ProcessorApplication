using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

using FileTransferModule.Configuration;
using FileTransferModule.Models;
using FileTransferModule.Models.Torrent;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using P2PModule.Interfaces;
using P2PModule.Services.Persistence;

namespace FileTransferModule.Services;


/// <summary>
/// Manages locally hosted, tracked, and seeded files.
/// Ingests new files, creates .ptorrent manifests, and controls swarm seeding.
/// </summary>
public interface IFileArchiveService
{
    /// <summary>
    /// Ingests a file, generates its .ptorrent manifest (2-5 MB blocks, 64 KB slices), registers it
    /// with local storage and the swarm orchestrator, and saves metadata. If copyToRepository is
    /// true, the source file is first copied into FileSharingSettings.StorageRootPath/repository
    /// (collision-safe naming) and the copy is what gets tracked/seeded, leaving the original
    /// untouched; if false, the file is tracked in place at sourceFilePath.
    /// </summary>
    Task<FileArchiveItemRecord> IngestAndTrackFileAsync(string sourceFilePath, bool isHidden = false, bool copyToRepository = false);

    /// <summary>
    /// Registers a file that was just assembled by a swarm download, reusing the manifest it was
    /// downloaded with (no rehash, same ContentHash, original InitialPeers kept), and starts
    /// seeding it. Returns the existing record unchanged if this ContentHash is already tracked.
    /// Fires OnFileIngested when not hidden, so it is announced like any other new local file.
    /// </summary>
    Task<FileArchiveItemRecord> RegisterDownloadedFileAsync(PTorrentManifest manifest, string localFilePath, bool isHidden = false);

    /// <summary>
    /// Retrieves all locally tracked files.
    /// </summary>
    Task<List<FileArchiveItemRecord>> GetTrackedFilesAsync(bool includeHidden = true);

    /// <summary>
    /// Retrieves a specific tracked file record by its ContentHash.
    /// </summary>
    Task<FileArchiveItemRecord?> GetTrackedFileAsync(string contentHash);

    /// <summary>
    /// Toggles the public visibility (IsHidden) of a tracked file. Kept for backward compatibility;
    /// UpdateRecordSettingsAsync covers this and more.
    /// </summary>
    Task<bool> ToggleVisibilityAsync(string contentHash);

    /// <summary>
    /// Updates one or more casual per-record settings. Only non-null parameters are changed. When
    /// allowDownload transitions, this actually starts/stops seeding via ISwarmOrchestratorService
    /// (not just a stored flag) - "available for download" reflects real seeding state.
    /// description/customFields are what CatalogGetFileInfo hands out to a peer that explicitly
    /// asks about this file - passing customFields replaces the whole set, not a merge.
    /// </summary>
    Task<bool> UpdateRecordSettingsAsync(string contentHash, bool? isHidden = null, bool? allowDownload = null, string? description = null, Dictionary<string, string>? customFields = null);

    /// <summary>
    /// Stops seeding and removes a file from local tracking.
    /// </summary>
    Task<bool> DeleteTrackedFileAsync(string contentHash, bool deletePhysicalFile = false);

    /// <summary>
    /// Fires after a non-hidden file finishes ingestion and is ready to seed. Used by
    /// FileTrackerAnnounceService to announce it to known "file_tracker" peers without
    /// FileArchiveService needing to know that service exists.
    /// </summary>
    event Action<FileArchiveItemRecord>? OnFileIngested;

    /// <summary>
    /// Fires when an already-tracked file becomes publicly available again - i.e. it transitions
    /// into (not hidden AND AllowDownload) via ToggleVisibilityAsync or UpdateRecordSettingsAsync.
    /// Lets FileTrackerAnnounceService announce it right away instead of waiting for the next
    /// periodic re-announce.
    /// </summary>
    event Action<FileArchiveItemRecord>? OnFileBecameAvailable;
}


public class FileArchiveService : IFileArchiveService
{
    private readonly IOptionsMonitor<FileSharingSettings> _settings;
    private readonly ISwarmOrchestratorService _swarmOrchestrator;
    private readonly INodeIdentityService _identity;
    private readonly ILogger<FileArchiveService> _logger;

    private readonly ConcurrentDictionary<string, FileArchiveItemRecord> _trackedItems = new();
    private readonly string _recordsIndexPath;

    public event Action<FileArchiveItemRecord>? OnFileIngested;
    public event Action<FileArchiveItemRecord>? OnFileBecameAvailable;

    private static bool IsPubliclyAvailable(FileArchiveItemRecord r) => !r.IsHidden && r.AllowDownload;

    private void RaiseBecameAvailable(FileArchiveItemRecord record)
    {
        try { OnFileBecameAvailable?.Invoke(record); }
        catch (Exception ex) { _logger.LogWarning(ex, "[FileArchive] OnFileBecameAvailable subscriber threw for {Hash}.", record.Manifest?.ContentHash); }
    }

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

    public async Task<FileArchiveItemRecord> IngestAndTrackFileAsync(string sourceFilePath, bool isHidden = false, bool copyToRepository = false)
    {
        if (!File.Exists(sourceFilePath))
            throw new FileNotFoundException("File not found for archive ingestion.", sourceFilePath);

        string trackedFilePath = sourceFilePath;
        if (copyToRepository)
        {
            trackedFilePath = CopyIntoRepository(sourceFilePath);
        }

        var localPeerDesc = new TorrentPeerDescriptor
        {
            NodeId = _identity.NodeId,
            SignalingEndpoints = _identity.GetEndpoints()
        };

        // 1. Generate clean PTorrentManifest (.ptorrent)
        var manifest = await PTorrentManifest.CreateFromFileAsync(
            trackedFilePath,
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
            LocalFilePath = trackedFilePath,
            ManifestFilePath = manifestPath,
            IsHidden = isHidden,
            AllowDownload = true,
            RegisteredUtc = DateTime.UtcNow
        };

        _trackedItems[manifest.ContentHash] = record;
        await SaveIndexRecordsAsync();

        // 4. Start seeding in Swarm
        await _swarmOrchestrator.StartSeedingAsync(manifest, trackedFilePath);

        _logger.LogInformation("[FileArchive] Ingested and tracked file: {Name} (Hash: {Hash}, Hidden: {Hidden}, Copied: {Copied})",
            manifest.FileName, manifest.ContentHash, isHidden, copyToRepository);

        if (!isHidden)
        {
            try { OnFileIngested?.Invoke(record); }
            catch (Exception ex) { _logger.LogWarning(ex, "[FileArchive] OnFileIngested subscriber threw for {Hash}.", manifest.ContentHash); }
        }

        return record;
    }

    public async Task<FileArchiveItemRecord> RegisterDownloadedFileAsync(PTorrentManifest manifest, string localFilePath, bool isHidden = false)
    {
        if (manifest == null || string.IsNullOrWhiteSpace(manifest.ContentHash))
            throw new ArgumentException("A manifest with a ContentHash is required.", nameof(manifest));
        if (!File.Exists(localFilePath))
            throw new FileNotFoundException("Downloaded file not found for archive registration.", localFilePath);

        if (_trackedItems.TryGetValue(manifest.ContentHash, out var existing))
        {
            _logger.LogInformation("[FileArchive] Downloaded file {Hash} is already tracked at {Path} - not re-registering.", manifest.ContentHash, existing.LocalFilePath);
            return existing;
        }

        string manifestsDir = _settings.CurrentValue.ManifestsPath;
        Directory.CreateDirectory(manifestsDir);
        string manifestPath = Path.Combine(manifestsDir, $"{manifest.ContentHash}.ptorrent");
        await File.WriteAllTextAsync(manifestPath, manifest.SerializeToJson());

        var record = new FileArchiveItemRecord
        {
            Manifest = manifest,
            LocalFilePath = localFilePath,
            ManifestFilePath = manifestPath,
            IsHidden = isHidden,
            AllowDownload = true,
            RegisteredUtc = DateTime.UtcNow
        };

        if (!_trackedItems.TryAdd(manifest.ContentHash, record))
        {
            // Lost a race with a concurrent registration of the same hash.
            return _trackedItems[manifest.ContentHash];
        }

        await SaveIndexRecordsAsync();
        await _swarmOrchestrator.StartSeedingAsync(manifest, localFilePath);

        _logger.LogInformation("[FileArchive] Registered downloaded file {Name} (Hash: {Hash}) for seeding.", manifest.FileName, manifest.ContentHash);

        if (!isHidden)
        {
            try { OnFileIngested?.Invoke(record); }
            catch (Exception ex) { _logger.LogWarning(ex, "[FileArchive] OnFileIngested subscriber threw for {Hash}.", manifest.ContentHash); }
        }

        return record;
    }

    /// <summary>Copies a file into StorageRootPath/repository, avoiding name collisions.</summary>
    private string CopyIntoRepository(string sourceFilePath)
    {
        string repoDir = Path.Combine(_settings.CurrentValue.StorageRootPath, "repository");
        Directory.CreateDirectory(repoDir);

        string fileName = Path.GetFileName(sourceFilePath);
        string destPath = Path.Combine(repoDir, fileName);

        if (File.Exists(destPath))
        {
            string nameOnly = Path.GetFileNameWithoutExtension(fileName);
            string ext = Path.GetExtension(fileName);
            destPath = Path.Combine(repoDir, $"{nameOnly}_{Guid.NewGuid():N}{ext}");
        }

        File.Copy(sourceFilePath, destPath, overwrite: false);
        return destPath;
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
            bool wasAvailable = IsPubliclyAvailable(record);
            record.IsHidden = !record.IsHidden;
            await SaveIndexRecordsAsync();
            _logger.LogInformation("[FileArchive] Toggled visibility for {Hash}. Hidden={Hidden}", contentHash, record.IsHidden);
            if (!wasAvailable && IsPubliclyAvailable(record)) RaiseBecameAvailable(record);
            return true;
        }
        return false;
    }

    public async Task<bool> UpdateRecordSettingsAsync(string contentHash, bool? isHidden = null, bool? allowDownload = null, string? description = null, Dictionary<string, string>? customFields = null)
    {
        if (!_trackedItems.TryGetValue(contentHash, out var record)) return false;

        bool wasAvailable = IsPubliclyAvailable(record);

        if (isHidden.HasValue) record.IsHidden = isHidden.Value;
        if (description != null) record.Description = description;
        if (customFields != null) record.CustomFields = customFields;

        if (allowDownload.HasValue && allowDownload.Value != record.AllowDownload)
        {
            record.AllowDownload = allowDownload.Value;

            if (record.AllowDownload)
            {
                await _swarmOrchestrator.StartSeedingAsync(record.Manifest, record.LocalFilePath);
            }
            else
            {
                await _swarmOrchestrator.StopSeedingAsync(contentHash);
            }
        }

        await SaveIndexRecordsAsync();
        _logger.LogInformation("[FileArchive] Updated settings for {Hash}: Hidden={Hidden}, AllowDownload={AllowDownload}",
            contentHash, record.IsHidden, record.AllowDownload);

        // Seeding (if re-enabled) has already started above, so announcing now is safe.
        if (!wasAvailable && IsPubliclyAvailable(record)) RaiseBecameAvailable(record);
        return true;
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
                            if (item.AllowDownload)
                            {
                                _ = _swarmOrchestrator.StartSeedingAsync(item.Manifest, item.LocalFilePath);
                            }
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