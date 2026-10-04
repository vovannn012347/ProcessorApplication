namespace FileTransferModule.Models;

/// <summary>
/// One lightweight catalog record: "host H has file F". Persisted as its own small JSON file at
/// {CatalogPath}/{HostNodeId}/{FileIdentifier}.json.
///
/// Block hashes never live here - they only ever travel as part of a .ptorrent manifest, fetched
/// through the dedicated download path (FetchManifestFromNodeAsync / TrackerGetManifest) when
/// someone actually wants to download the file. That manifest fully covers a file's parts (byte
/// ranges and their hashes) on its own, which is what makes arbitrary block lengths possible - the
/// catalog doesn't need to know or carry any of it.
///
/// "Full info" here means something else entirely: a user-provided free-text description and a
/// flat set of custom fields, set by whoever hosts the file. Like the rest of this record, it's
/// never carried by consensus - only returned to a peer that explicitly asks for it
/// (CatalogGetFileInfo), and FullInfoFetchedUtc records when we last did that.
/// </summary>
public class FileCatalogRecord
{
    /// <summary>The file's content hash - its identity, and the file name on disk.</summary>
    public string FileIdentifier { get; set; } = string.Empty;

    /// <summary>The node that hosts the file - the directory name on disk.</summary>
    public string HostNodeId { get; set; } = string.Empty;

    /// <summary>Human-readable name.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>When the host says this record was last updated. Host-declared; used for newer-wins ordering.</summary>
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Local bookkeeping only - never trusted from the wire. Last time any peer confirmed this
    /// record is still present; drives eviction (FileSharingSettings.CatalogRecordStaleAfterHours).
    /// </summary>
    public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;

    /// <summary>User-provided free-text description. Null until explicitly fetched.</summary>
    public string? Description { get; set; }

    /// <summary>User-provided custom fields (flat, name to text value). Empty until explicitly fetched.</summary>
    public Dictionary<string, string> CustomFields { get; set; } = new();

    /// <summary>
    /// When Description/CustomFields were last explicitly fetched from the host - null means never.
    /// Distinguishes "asked and got nothing" from "never asked".
    /// </summary>
    public DateTime? FullInfoFetchedUtc { get; set; }
}
