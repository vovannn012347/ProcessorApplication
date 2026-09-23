using FileSharingModule.Models.Torrent;

namespace FileSharingModule.Models;

public class FileCatalogEntry
{
    public PTorrentManifest Manifest { get; set; } = new();
    public string HostNodeId { get; set; } = string.Empty;
    public List<string> HostSignalingEndpoints { get; set; } = new();
    public bool IsLocalSeeding { get; set; }
    public DateTime DiscoveredUtc { get; set; } = DateTime.UtcNow;
}