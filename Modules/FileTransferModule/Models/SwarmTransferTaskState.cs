namespace FileTransferModule.Models;

public enum SwarmTaskStatus
{
    Queued,
    Ingesting,
    Staging,
    Assembling,
    Complete,
    Paused,
    Failed
}

/// <summary>
/// Active runtime telemetry container tracking download/upload tasks and bitfield visualizer states.
/// </summary>
public class SwarmTransferTaskState
{
    /// <summary>
    /// The id PauseDownloadAsync/ResumeDownloadAsync/CancelDownloadAsync/GetBitfieldState actually
    /// key on - a random id assigned per download attempt (Guid.NewGuid() in StartDownloadAsync),
    /// deliberately separate from ContentHash so downloading the same file twice (e.g. a retry) has
    /// two independent, individually controllable tasks.
    /// </summary>
    public string TaskId { get; set; } = string.Empty;
    public string ContentHash { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public long TotalSizeBytes { get; set; }
    public string TargetSavePath { get; set; } = string.Empty;
    public SwarmBitfield Bitfield { get; set; } = new(0);

    public SwarmTaskStatus Status { get; set; } = SwarmTaskStatus.Queued;
    public double ProgressPercentage => Bitfield.GetProgressPercentage();
    public double DownloadSpeedBps { get; set; }
    public double UploadSpeedBps { get; set; }

    /// <summary>Bytes received for this task (accepted slices) since it started.</summary>
    public long TotalDownloadedBytes { get; set; }
    /// <summary>Bytes this node served to peers of the same file since the node started.</summary>
    public long TotalUploadedBytes { get; set; }
    public long DownloadedLastMinuteBytes { get; set; }
    public long UploadedLastMinuteBytes { get; set; }
    public int ActiveSeedersCount { get; set; }
    public List<string> ConnectedPeerIds { get; set; } = new();

    public string ErrorMessage { get; set; } = string.Empty;
    public DateTime StartedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedUtc { get; set; }
}