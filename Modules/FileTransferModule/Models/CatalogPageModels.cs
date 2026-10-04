namespace FileTransferModule.Models;

/// <summary>
/// Request for a paginated catalog listing - used both for direct node-to-node probing
/// (CatalogGetLocalEntriesPaged) and for the local combined view.
/// </summary>
public class CatalogPageRequest
{
    public int Page { get; set; } = 0;
    public int PageSize { get; set; } = 50;
}

/// <summary>
/// Paginated catalog listing result. Entries returned over the wire (from a direct node probe)
/// are always lightweight shells - ContentHash/FileName/TotalSizeBytes/CreatedAtUtc only, no
/// block hashes. Those only ever travel through the dedicated download path
/// (FetchManifestFromNodeAsync).
/// </summary>
public class CatalogPageResponse
{
    public List<FileCatalogEntry> Entries { get; set; } = new();
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int TotalCount { get; set; }
    public bool HasMore { get; set; }
    /// <summary>
    /// For a direct node catalogue request: how many of the returned entries were saved locally
    /// (subject to the per-host cap). Entries beyond it are still returned for display, just not
    /// stored. Always 0 for a purely local listing.
    /// </summary>
    public int SavedCount { get; set; }
}

/// <summary>Request for one file's description/custom fields from its host (CatalogGetFileInfo).</summary>
public class CatalogFileInfoRequest
{
    public string FileIdentifier { get; set; } = string.Empty;
}

/// <summary>Response to CatalogFileInfoRequest. Found=false means the host doesn't have this file (anymore).</summary>
public class CatalogFileInfoResponse
{
    public bool Found { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string? Description { get; set; }
    public Dictionary<string, string> CustomFields { get; set; } = new();
}

/// <summary>
/// Outcome of explicitly downloading a file's info (description/custom fields - never the
/// manifest, see class remarks on FileCatalogConsensusChannel) from its host.
/// </summary>
public class FileInfoDownloadResult
{
    public bool Success { get; set; }
    /// <summary>True if the info was stored locally; false if it was fetched but not saved (e.g. per-host cap reached).</summary>
    public bool Saved { get; set; }
    public string? Message { get; set; }
    public FileCatalogEntry? Entry { get; set; }
}
