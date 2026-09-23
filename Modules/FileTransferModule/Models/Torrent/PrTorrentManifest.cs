using System.Security.Cryptography;
using System.Text.Json;

namespace FileSharingModule.Models.Torrent;
/// <summary>
/// The .ptorrent (ProcessorTorrentManifest) container defining hierarchical block/slice structures,
/// root SHA-256 InfoHash, and seeder descriptors.
/// Purely immutable file structure descriptor.
/// </summary>
public class PTorrentManifest
{
    /// <summary>
    /// Unique SHA-256 Root InfoHash generated from all block hashes combined.
    /// </summary>
    public string ContentHash { get; set; } = string.Empty;

    public string FileName { get; set; } = string.Empty;
    public long TotalSizeBytes { get; set; }
    public int BlockSizeBytes { get; set; } = 2097152; // Default 2 MB block
    public int SliceSizeBytes { get; set; } = 65536;   // Default 64 KB wire slice

    /// <summary>
    /// SHA-256 hex hashes for each block in index order.
    /// </summary>
    public List<string> BlockHashes { get; set; } = new();

    /// <summary>
    /// Initial seeder peers registered upon file manifest creation.
    /// </summary>
    public List<TorrentPeerDescriptor> InitialPeers { get; set; } = new();

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public int TotalBlocksCount => BlockHashes.Count;

    public int GetSlicesCountForBlock(int blockIndex)
    {
        if (blockIndex < 0 || blockIndex >= BlockHashes.Count) return 0;

        long blockOffset = (long)blockIndex * BlockSizeBytes;
        long remainingFileBytes = TotalSizeBytes - blockOffset;
        long currentBlockLength = Math.Min(BlockSizeBytes, remainingFileBytes);

        return (int)Math.Ceiling((double)currentBlockLength / SliceSizeBytes);
    }

    public int GetBlockLength(int blockIndex)
    {
        if (blockIndex < 0 || blockIndex >= BlockHashes.Count) return 0;

        long blockOffset = (long)blockIndex * BlockSizeBytes;
        long remainingFileBytes = TotalSizeBytes - blockOffset;
        return (int)Math.Min(BlockSizeBytes, remainingFileBytes);
    }

    public static async Task<PTorrentManifest> CreateFromFileAsync(
        string filePath,
        List<TorrentPeerDescriptor> initialSeeders,
        int blockSizeBytes = 2097152,
        int sliceSizeBytes = 65536)
    {
        var fileInfo = new FileInfo(filePath);
        if (!fileInfo.Exists)
        {
            throw new FileNotFoundException("Source file for manifest generation not found.", filePath);
        }

        var manifest = new PTorrentManifest
        {
            FileName = fileInfo.Name,
            TotalSizeBytes = fileInfo.Length,
            BlockSizeBytes = blockSizeBytes,
            SliceSizeBytes = sliceSizeBytes,
            InitialPeers = initialSeeders ?? new List<TorrentPeerDescriptor>(),
            CreatedAtUtc = DateTime.UtcNow
        };

        using var sha256 = SHA256.Create();
        using var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, true);

        byte[] blockBuffer = new byte[blockSizeBytes];
        int bytesRead;
        using var rootCombinedHashesStream = new MemoryStream();

        while ((bytesRead = await fileStream.ReadAsync(blockBuffer, 0, blockBuffer.Length)) > 0)
        {
            byte[] blockHash = sha256.ComputeHash(blockBuffer, 0, bytesRead);
            string hexHash = Convert.ToHexString(blockHash).ToLowerInvariant();

            manifest.BlockHashes.Add(hexHash);
            await rootCombinedHashesStream.WriteAsync(blockHash, 0, blockHash.Length);
        }

        byte[] rootHashBytes = sha256.ComputeHash(rootCombinedHashesStream.ToArray());
        manifest.ContentHash = Convert.ToHexString(rootHashBytes).ToLowerInvariant();

        return manifest;
    }

    public string SerializeToJson()
    {
        return JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
    }

    public static PTorrentManifest? DeserializeFromJson(string json)
    {
        return JsonSerializer.Deserialize<PTorrentManifest>(json);
    }
}