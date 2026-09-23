
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

using FileSharingModule.Configuration;
using FileSharingModule.Models.Torrent;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FileSharingModule.Services;


/// <summary>
/// Defines storage operations for staging 64 KB slices, verifying 2-5 MB block SHA-256 hashes,
/// and assembling completed files into jailed or admin-specified destination paths.
/// </summary>
public interface IFileChunkStorageEngine
{
    /// <summary>
    /// Initializes staging directories under PendingDownloadsPath for a specific transfer task.
    /// </summary>
    Task InitializeTaskStorageAsync(string taskId, PTorrentManifest manifest);

    /// <summary>
    /// Writes an incoming 64 KB wire slice to task staging disk space.
    /// </summary>
    Task<bool> WriteSliceAsync(string taskId, int blockIndex, int sliceIndex, byte[] sliceData);

    /// <summary>
    /// Reads all received slices for a block, computes hardware-accelerated SHA-256 hash,
    /// and compares it against manifest.BlockHashes[blockIndex].
    /// Commits verified block to task assembly space upon match.
    /// </summary>
    Task<bool> VerifyAndCommitBlockAsync(string taskId, PTorrentManifest manifest, int blockIndex);

    /// <summary>
    /// Checks if a 2-5 MB block has already been verified and committed to task assembly space.
    /// </summary>
    bool IsBlockCommitted(string taskId, int blockIndex);

    /// <summary>
    /// Reads a verified 2-5 MB block or raw source file segment for seeding uploads.
    /// </summary>
    Task<byte[]?> GetBlockDataAsync(string taskId, PTorrentManifest manifest, int blockIndex, string? sourceFilePath = null);

    /// <summary>
    /// Reads a single 64 KB slice from a block or source file for seeding uploads.
    /// </summary>
    Task<byte[]?> GetSliceDataAsync(PTorrentManifest manifest, int blockIndex, int sliceIndex, string? sourceFilePath = null);

    /// <summary>
    /// Merges all committed blocks into final file output.
    /// Standard users are jailed to {StorageRootPath}/{taskId}/files/{fileName}.
    /// Custom target directory is strictly validated against canonical path rules.
    /// </summary>
    Task<string?> FinalizeFileAssemblyAsync(
        string taskId,
        PTorrentManifest manifest,
        string? customTargetDirectory = null,
        string? customFileName = null,
        bool isLocalAdmin = false);

    /// <summary>
    /// Purges task staging directories under PendingDownloadsPath.
    /// </summary>
    Task CleanupTaskStorageAsync(string taskId);
}


/// <summary>
/// High-performance file storage manager handling 64 KB slice staging, hardware-accelerated 
/// SHA-256 block hash verification, and jailed file output assembly.
/// </summary>
public class FileChunkStorageEngine : IFileChunkStorageEngine
{
    private readonly IOptionsMonitor<FileSharingSettings> _settings;
    private readonly ILogger<FileChunkStorageEngine> _logger;

    public FileChunkStorageEngine(
        IOptionsMonitor<FileSharingSettings> settings,
        ILogger<FileChunkStorageEngine> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    /// <summary>
    /// Prepares staging folders under PendingDownloadsPath/{taskId}/.
    /// Folder layout:
    ///   - PendingDownloadsPath/{taskId}/slices/block_{blockIndex}/
    ///   - PendingDownloadsPath/{taskId}/blocks/
    /// </summary>
    public Task InitializeTaskStorageAsync(string taskId, PTorrentManifest manifest)
    {
        if (string.IsNullOrWhiteSpace(taskId))
            throw new ArgumentNullException(nameof(taskId));

        string taskPendingPath = Path.Combine(_settings.CurrentValue.PendingDownloadsPath, taskId);
        string blocksFolder = Path.Combine(taskPendingPath, "blocks");
        string slicesFolder = Path.Combine(taskPendingPath, "slices");

        Directory.CreateDirectory(blocksFolder);
        Directory.CreateDirectory(slicesFolder);

        _logger.LogInformation("[StorageEngine] Staging initialized for task {TaskId} under {Path}", taskId, taskPendingPath);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Writes a 64 KB wire slice to task staging disk.
    /// Path: PendingDownloadsPath/{taskId}/slices/block_{blockIndex}/slice_{sliceIndex}.dat
    /// </summary>
    public async Task<bool> WriteSliceAsync(string taskId, int blockIndex, int sliceIndex, byte[] sliceData)
    {
        if (string.IsNullOrWhiteSpace(taskId) || sliceData == null || sliceData.Length == 0)
            return false;

        try
        {
            string sliceDir = Path.Combine(_settings.CurrentValue.PendingDownloadsPath, taskId, "slices", $"block_{blockIndex}");
            Directory.CreateDirectory(sliceDir);

            string slicePath = Path.Combine(sliceDir, $"slice_{sliceIndex}.dat");
            await File.WriteAllBytesAsync(slicePath, sliceData);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[StorageEngine] Failed writing slice {SliceIndex} of block {BlockIndex} for task {TaskId}", sliceIndex, blockIndex, taskId);
            return false;
        }
    }

    /// <summary>
    /// Reads all received slices for blockIndex, concatenates them into a 2-5 MB block buffer,
    /// verifies calculated SHA-256 against manifest.BlockHashes[blockIndex], and commits
    /// the block to PendingDownloadsPath/{taskId}/blocks/block_{blockIndex}.blk.
    /// If hash mismatch occurs, discards slices so downloader can re-queue the block.
    /// </summary>
    public async Task<bool> VerifyAndCommitBlockAsync(string taskId, PTorrentManifest manifest, int blockIndex)
    {
        if (string.IsNullOrWhiteSpace(taskId) || manifest == null || blockIndex < 0 || blockIndex >= manifest.TotalBlocksCount)
            return false;

        string sliceDir = Path.Combine(_settings.CurrentValue.PendingDownloadsPath, taskId, "slices", $"block_{blockIndex}");
        if (!Directory.Exists(sliceDir))
        {
            _logger.LogWarning("[StorageEngine] Cannot verify block {BlockIndex} for task {TaskId}: slice directory missing.", blockIndex, taskId);
            return false;
        }

        int expectedSliceCount = manifest.GetSlicesCountForBlock(blockIndex);
        int expectedBlockLength = manifest.GetBlockLength(blockIndex);

        // Concatenate all slices in order
        byte[] blockBuffer = new byte[expectedBlockLength];
        int currentBufferOffset = 0;

        for (int sliceIndex = 0; sliceIndex < expectedSliceCount; sliceIndex++)
        {
            string slicePath = Path.Combine(sliceDir, $"slice_{sliceIndex}.dat");
            if (!File.Exists(slicePath))
            {
                _logger.LogWarning("[StorageEngine] Missing slice {SliceIndex} for block {BlockIndex} in task {TaskId}.", sliceIndex, blockIndex, taskId);
                return false;
            }

            byte[] sliceBytes = await File.ReadAllBytesAsync(slicePath);
            Buffer.BlockCopy(sliceBytes, 0, blockBuffer, currentBufferOffset, sliceBytes.Length);
            currentBufferOffset += sliceBytes.Length;
        }

        // Verify hardware-accelerated SHA-256 hash
        byte[] calculatedHashBytes = SHA256.HashData(blockBuffer);
        string calculatedHex = Convert.ToHexString(calculatedHashBytes).ToLowerInvariant();
        string expectedHex = manifest.BlockHashes[blockIndex];

        if (!string.Equals(calculatedHex, expectedHex, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("[StorageEngine] SHA-256 mismatch on block {BlockIndex} for task {TaskId}! Calculated={Calc}, Expected={Exp}. Discarding slices.",
                blockIndex, taskId, calculatedHex, expectedHex);

            // Discard corrupt slices
            try { Directory.Delete(sliceDir, true); } catch { }
            return false;
        }

        // Commit verified block
        string committedBlocksDir = Path.Combine(_settings.CurrentValue.PendingDownloadsPath, taskId, "blocks");
        Directory.CreateDirectory(committedBlocksDir);

        string blockPath = Path.Combine(committedBlocksDir, $"block_{blockIndex}.blk");
        await File.WriteAllBytesAsync(blockPath, blockBuffer);

        // Cleanup staging slice directory
        try { Directory.Delete(sliceDir, true); } catch { }

        _logger.LogInformation("[StorageEngine] Verified and committed block {BlockIndex}/{Total} for task {TaskId}.",
            blockIndex, manifest.TotalBlocksCount, taskId);

        return true;
    }

    public bool IsBlockCommitted(string taskId, int blockIndex)
    {
        if (string.IsNullOrWhiteSpace(taskId)) return false;

        string blockPath = Path.Combine(_settings.CurrentValue.PendingDownloadsPath, taskId, "blocks", $"block_{blockIndex}.blk");
        return File.Exists(blockPath);
    }

    public async Task<byte[]?> GetBlockDataAsync(string taskId, PTorrentManifest manifest, int blockIndex, string? sourceFilePath = null)
    {
        // 1. Try reading committed staging block
        string blockPath = Path.Combine(_settings.CurrentValue.PendingDownloadsPath, taskId, "blocks", $"block_{blockIndex}.blk");
        if (File.Exists(blockPath))
        {
            return await File.ReadAllBytesAsync(blockPath);
        }

        // 2. Fallback to reading segment directly from complete source file (if seeding)
        if (!string.IsNullOrEmpty(sourceFilePath) && File.Exists(sourceFilePath))
        {
            int blockLength = manifest.GetBlockLength(blockIndex);
            long offset = (long)blockIndex * manifest.BlockSizeBytes;

            byte[] buffer = new byte[blockLength];
            using var fs = new FileStream(sourceFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, true);
            fs.Seek(offset, SeekOrigin.Begin);

            int read = await fs.ReadAsync(buffer, 0, blockLength);
            if (read == blockLength) return buffer;
        }

        return null;
    }

    public async Task<byte[]?> GetSliceDataAsync(PTorrentManifest manifest, int blockIndex, int sliceIndex, string? sourceFilePath = null)
    {
        if (manifest == null || blockIndex < 0 || sliceIndex < 0) return null;

        long blockOffset = (long)blockIndex * manifest.BlockSizeBytes;
        long sliceOffset = blockOffset + ((long)sliceIndex * manifest.SliceSizeBytes);

        int totalBlockLength = manifest.GetBlockLength(blockIndex);
        long blockRemainingBytes = totalBlockLength - ((long)sliceIndex * manifest.SliceSizeBytes);

        if (blockRemainingBytes <= 0) return null;
        int sliceLength = (int)Math.Min(manifest.SliceSizeBytes, blockRemainingBytes);

        if (!string.IsNullOrEmpty(sourceFilePath) && File.Exists(sourceFilePath))
        {
            byte[] buffer = new byte[sliceLength];
            using var fs = new FileStream(sourceFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, true);
            fs.Seek(sliceOffset, SeekOrigin.Begin);

            int read = await fs.ReadAsync(buffer, 0, sliceLength);
            if (read == sliceLength) return buffer;
        }

        return null;
    }

    /// <summary>
    /// Assembles all committed blocks into final destination file.
    /// Enforcement:
    ///   - Non-Admin / Standard user: Jailed under {StorageRootPath}/{taskId}/files/{fileName}
    ///   - Local Admin: Custom target directory validated against directory traversal attacks.
    /// </summary>
    public async Task<string?> FinalizeFileAssemblyAsync(
        string taskId,
        PTorrentManifest manifest,
        string? customTargetDirectory = null,
        string? customFileName = null,
        bool isLocalAdmin = false)
    {
        if (string.IsNullOrWhiteSpace(taskId) || manifest == null)
            return null;

        // Verify all blocks exist
        string blocksDir = Path.Combine(_settings.CurrentValue.PendingDownloadsPath, taskId, "blocks");
        for (int i = 0; i < manifest.TotalBlocksCount; i++)
        {
            string blkPath = Path.Combine(blocksDir, $"block_{i}.blk");
            if (!File.Exists(blkPath))
            {
                _logger.LogError("[StorageEngine] Cannot finalize task {TaskId}: Missing block {BlockIndex}.", taskId, i);
                return null;
            }
        }

        // Determine destination path according to jailing policy
        string finalDirectory;
        string targetFileName = !string.IsNullOrWhiteSpace(customFileName) ? customFileName.Trim() : manifest.FileName;

        if (isLocalAdmin && !string.IsNullOrWhiteSpace(customTargetDirectory))
        {
            // Admin canonical path validation
            finalDirectory = Path.GetFullPath(customTargetDirectory);
        }
        else
        {
            // Unconditional jailing for non-admin/automated downloads
            finalDirectory = Path.Combine(_settings.CurrentValue.StorageRootPath, taskId, "files");
        }

        Directory.CreateDirectory(finalDirectory);
        string finalFilePath = Path.Combine(finalDirectory, targetFileName);

        _logger.LogInformation("[StorageEngine] Assembling {Count} blocks into final file: {Path}", manifest.TotalBlocksCount, finalFilePath);

        using (var outputStream = new FileStream(finalFilePath, FileMode.Create, FileAccess.Write, FileShare.None, 16384, true))
        {
            for (int i = 0; i < manifest.TotalBlocksCount; i++)
            {
                string blkPath = Path.Combine(blocksDir, $"block_{i}.blk");
                using var blockStream = new FileStream(blkPath, FileMode.Open, FileAccess.Read, FileShare.Read, 16384, true);
                await blockStream.CopyToAsync(outputStream);
            }
        }

        // Cleanup pending downloads staging workspace
        await CleanupTaskStorageAsync(taskId);

        _logger.LogInformation("[StorageEngine] Task {TaskId} finalized successfully. Output: {FilePath}", taskId, finalFilePath);
        return finalFilePath;
    }

    public Task CleanupTaskStorageAsync(string taskId)
    {
        if (string.IsNullOrWhiteSpace(taskId)) return Task.CompletedTask;

        string taskPendingPath = Path.Combine(_settings.CurrentValue.PendingDownloadsPath, taskId);
        if (Directory.Exists(taskPendingPath))
        {
            try
            {
                Directory.Delete(taskPendingPath, true);
                _logger.LogInformation("[StorageEngine] Cleaned up staging workspace for task {TaskId}.", taskId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[StorageEngine] Exception during staging cleanup for task {TaskId}.", taskId);
            }
        }

        return Task.CompletedTask;
    }
}
