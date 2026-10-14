using FileTransferModule.Models.Torrent;

namespace FileTransferModule.Models;

/// <summary>
/// Persistent record container representing a locally ingested file actively tracked and seeded.
/// </summary>
public class FileArchiveItemRecord
{
    public PTorrentManifest Manifest { get; set; } = new();
    public string LocalFilePath { get; set; } = string.Empty;
    public string ManifestFilePath { get; set; } = string.Empty;
    /// <summary>Not included in catalog listings/announces when true. Casual visibility toggle.</summary>
    public bool IsHidden { get; set; }
    /// <summary>
    /// Whether this node actively serves slice requests for this file. Independent of IsHidden -
    /// a file can be visible in the catalog but not currently downloadable (paused), or hidden yet
    /// still downloadable to someone who already knows the hash. Defaults true (existing behavior
    /// for anything ingested before this flag existed, via JSON deserialization default).
    /// </summary>
    public bool AllowDownload { get; set; } = true;
    /// <summary>
    /// User-provided free-text description. Never carried by consensus or catalog browsing - only
    /// returned to a peer that explicitly asks for this file's info (CatalogGetFileInfo).
    /// </summary>
    public string? Description { get; set; }
    /// <summary>Same rule as Description: flat, user-provided, explicit-request only.</summary>
    public Dictionary<string, string> CustomFields { get; set; } = new();
    public DateTime RegisteredUtc { get; set; } = DateTime.UtcNow;
}