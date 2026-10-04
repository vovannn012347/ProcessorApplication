using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using FileTransferModule.Models;

namespace FileTransferModule.Configuration;

public class FileSharingSettings
{
    /// <summary>
    /// Default root directory where completed tracked files and download assets are stored if user does not select directory.
    /// Default: ./Module_Data/FileSharing/Storage
    /// </summary>
    public string StorageRootPath { get; set; } = "./Module_Data/FileSharing/Storage";

    /// <summary>
    /// Staging directory for incomplete, in-flight downloads. Parts of files, etc
    /// Default: ./Module_Data/FileSharing/Pending
    /// </summary>
    public string PendingDownloadsPath { get; set; } = "./Module_Data/FileSharing/Pending";

    /// <summary>
    /// Directory where .ptorrent manifest files are stored.
    /// Default: ./Module_Data/FileSharing/Manifests
    /// </summary>
    public string ManifestsPath { get; set; } = "./Module_Data/FileSharing/Manifests";

    /// <summary>
    /// Root of the per-node catalogue store. Each remote host gets a directory named after its
    /// NodeId, holding one small JSON record per file it hosts:
    /// {CatalogPath}/{NodeId}/{FileIdentifier}.json
    /// Default: ./Module_Data/FileSharing/Catalog
    /// </summary>
    public string CatalogPath { get; set; } = "./Module_Data/FileSharing/Catalog";

    /// <summary>
    /// Size of a primary verification block in bytes (e.g. 2 MB = 2,097,152 bytes; default 5 242 880).
    /// </summary>
    public int BlockSizeBytes { get; set; } = 5242880;

    /// <summary>
    /// Size of a single WebRTC wire slice in bytes (e.g. 64 KB = 65,536 bytes; default 65,536).
    /// </summary>
    public int SliceSizeBytes { get; set; } = 65536;

    /// <summary>
    /// Maximum concurrent active file upload lanes allowed.
    /// </summary>
    public int MaxActiveUploads { get; set; } = 10;

    /// <summary>
    /// Maximum concurrent active file download tasks allowed.
    /// </summary>
    public int MaxActiveDownloads { get; set; } = 5;

    /// <summary>
    /// Maximum concurrent peer connections per active file download task.
    /// </summary>
    public int MaxPeerConnectionsPerFile { get; set; } = 8;

    /// <summary>
    /// Timeout in seconds before a slice request to a peer is considered lost and re-queued.
    /// </summary>
    public int SliceRequestTimeoutSeconds { get; set; } = 10;

    /// <summary>
    /// Discovery timeout in seconds when searching for "file_tracker" nodes or seeders.
    /// </summary>
    public int SeederDiscoveryTimeoutSeconds { get; set; } = 15;

    /// <summary>
    /// Maximum number of catalog records we will accept and store per remote host - whether
    /// learned via consensus relay or a direct node query. Existing known records for a host are
    /// never removed by this cap; it only limits how many NEW (not-yet-known) records per host we
    /// accept beyond what we already have. Refreshes of already-known records don't count against
    /// it. Protects local storage from a single node (malicious or just very prolific) flooding
    /// our catalog.
    /// </summary>
    public int MaxCatalogRecordsPerHost { get; set; } = 20;

    /// <summary>
    /// Default page size used when directly probing a node's catalog (QueryNodeCatalogDirectAsync) -
    /// bounds how much we request/accept from a single direct query by default.
    /// </summary>
    public int DirectQueryDefaultPageSize { get; set; } = 20;

    /// <summary>
    /// A relay-cache catalog entry (one we don't host ourselves) is evicted once no peer has
    /// confirmed its presence (via a fresh accept or MarkRecordsStillPresentAsync) for this many
    /// hours. Frees room under MaxCatalogRecordsPerHost for genuinely new entries once stale ones
    /// age out. Does not apply to our own hosted files, which are never evicted this way.
    /// </summary>
    public int CatalogRecordStaleAfterHours { get; set; } = 72;

    /// <summary>
    /// How often (seconds) the catalogue store flushes batched record refreshes to disk and sweeps
    /// stale records. New/explicitly-downloaded records are written immediately; this only covers
    /// the "still present" refreshes that would otherwise rewrite many files on every sync pass.
    /// </summary>
    public int CatalogPersistDebounceSeconds { get; set; } = 30;

    /// <summary>
    /// Ordered access-list rules for the description/custom fields received from ANOTHER host via
    /// CatalogGetFileInfo (never applied to your own records - FileArchiveService.UpdateRecordSettingsAsync
    /// has no such restriction, you can put whatever you want in your own fields). Evaluated by
    /// FileCatalogConsensusChannel; see CustomFieldAccessRule for the exact matching rules. Empty
    /// by default, which combined with CustomFieldDefaultAction = Deny means nothing from another
    /// host is kept until you add rules explicitly allowing it.
    /// </summary>
    public List<CustomFieldAccessRule> CustomFieldAccessRules { get; set; } = new();

    /// <summary>
    /// What happens to a description/custom field from another host that no CustomFieldAccessRules
    /// entry matches. Default: Deny - an unrecognized field name from a peer is dropped rather than
    /// kept, until an admin explicitly writes a rule allowing it.
    /// </summary>
    public FieldRuleAction CustomFieldDefaultAction { get; set; } = FieldRuleAction.Deny;
}
