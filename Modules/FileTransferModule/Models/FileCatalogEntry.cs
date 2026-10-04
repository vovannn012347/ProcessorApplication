using FileTransferModule.Models.Torrent;

namespace FileTransferModule.Models;

public class FileCatalogEntry
{
    /// <summary>
    /// Always a shell for anything not hosted locally (ContentHash/FileName/TotalSizeBytes/
    /// CreatedAtUtc only, no BlockHashes) - catalog browsing never carries block hashes; those only
    /// ever arrive via the dedicated download path (FetchManifestFromNodeAsync). For our own local
    /// files this is the real manifest, since that costs nothing extra to include locally.
    /// </summary>
    public PTorrentManifest Manifest { get; set; } = new();
    public string HostNodeId { get; set; } = string.Empty;
    public List<string> HostSignalingEndpoints { get; set; } = new();
    public bool IsLocalSeeding { get; set; }
    /// <summary>When the host says this record was last updated - used for newer-wins ordering.</summary>
    public DateTime DiscoveredUtc { get; set; } = DateTime.UtcNow;
    /// <summary>User-provided free-text description, if fetched (see FullInfoFetchedUtc).</summary>
    public string? Description { get; set; }
    /// <summary>User-provided custom fields, if fetched (see FullInfoFetchedUtc).</summary>
    public Dictionary<string, string> CustomFields { get; set; } = new();
    /// <summary>
    /// When Description/CustomFields were last explicitly fetched from the host (CatalogGetFileInfo)
    /// - null means never asked. Unrelated to Manifest/block data, which is a separate concern.
    /// </summary>
    public DateTime? FullInfoFetchedUtc { get; set; }
}
