namespace FileTransferModule.Models;

/// <summary>One direction of traffic (download or upload), computed when read.</summary>
public sealed class TransferDirectionStats
{
    public double RateBps { get; init; }
    public long LastMinuteBytes { get; init; }
    public long TotalBytes { get; init; }

    public static readonly TransferDirectionStats Empty = new();
}

/// <summary>A file this node is currently (or was within the last minute) serving to peers.</summary>
public sealed class UploadFileStats
{
    public string ContentHash { get; init; } = string.Empty;
    public string FileName { get; init; } = string.Empty;
    public int ActiveChannels { get; init; }
    public TransferDirectionStats Upload { get; init; } = TransferDirectionStats.Empty;
}

/// <summary>Everything the Active Transfers page shows: node totals, files being served, download tasks.</summary>
public sealed class ExchangeTelemetryViewModel
{
    public List<SwarmTransferTaskState> Tasks { get; init; } = new();
    public List<UploadFileStats> Uploads { get; init; } = new();
    public TransferDirectionStats NodeDownload { get; init; } = TransferDirectionStats.Empty;
    public TransferDirectionStats NodeUpload { get; init; } = TransferDirectionStats.Empty;
    public int UploadSlotsInUse { get; init; }
    public int UploadSlotsMax { get; init; }
    public int ActiveDownloads { get; init; }
    public int MaxActiveDownloads { get; init; }

    /// <summary>Server time of this snapshot (ms since epoch) - the page uses it for "since last refresh".</summary>
    public long GeneratedUnixMs { get; init; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}
