namespace FileTransferModule.Models;

/// <summary>
/// One file to fetch as part of a batch download request.
/// </summary>
public class BatchDownloadRequestItem
{
    public string HostNodeId { get; set; } = string.Empty;
    public string ContentHash { get; set; } = string.Empty;
    public string? CustomFileName { get; set; }
}

/// <summary>
/// Request to start downloading several files at once, each possibly from a different host.
/// CustomDirectory applies to all items in the batch; use CustomFileName per-item for individual
/// renames.
/// </summary>
public class BatchDownloadRequest
{
    public List<BatchDownloadRequestItem> Items { get; set; } = new();
    public string? CustomDirectory { get; set; }
}

/// <summary>
/// Per-item outcome of a batch download request.
/// </summary>
public class BatchDownloadResultItem
{
    public string ContentHash { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string? TaskId { get; set; }
    public string? FileName { get; set; }
    public string? Message { get; set; }
}
