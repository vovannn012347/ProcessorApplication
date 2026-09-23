using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FileSharingModule.Configuration;

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
}
