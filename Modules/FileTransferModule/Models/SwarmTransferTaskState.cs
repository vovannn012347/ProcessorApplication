namespace FileSharingModule.Models;

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
    public string ContentHash { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public long TotalSizeBytes { get; set; }
    public string TargetSavePath { get; set; } = string.Empty;
    public SwarmBitfield Bitfield { get; set; } = new(0);

    public SwarmTaskStatus Status { get; set; } = SwarmTaskStatus.Queued;
    public double ProgressPercentage => Bitfield.GetProgressPercentage();
    public double DownloadSpeedBps { get; set; }
    public double UploadSpeedBps { get; set; }
    public int ActiveSeedersCount { get; set; }
    public List<string> ConnectedPeerIds { get; set; } = new();

    public string ErrorMessage { get; set; } = string.Empty;
    public DateTime StartedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedUtc { get; set; }
}