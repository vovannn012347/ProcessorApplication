using FileSharingModule.Models.Torrent;

namespace FileSharingModule.Models;

/// <summary>
/// Persistent record container representing a locally ingested file actively tracked and seeded.
/// </summary>
public class FileArchiveItemRecord
{
    public PTorrentManifest Manifest { get; set; } = new();
    public string LocalFilePath { get; set; } = string.Empty;
    public string ManifestFilePath { get; set; } = string.Empty;
    public bool IsHidden { get; set; }
    public long TotalUploadedBytes { get; set; }
    public DateTime RegisteredUtc { get; set; } = DateTime.UtcNow;
}