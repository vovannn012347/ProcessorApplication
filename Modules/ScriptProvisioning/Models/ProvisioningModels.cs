using System.Text.Json.Serialization;

namespace ScriptProvisioning.Models;

/// <summary>A build this node publishes (offers to other nodes as a package).</summary>
public sealed class PublishedPackage
{
    public int ScriptIndexId { get; set; }
    public string ScriptId { get; set; } = "";
    public string Version { get; set; } = "";
    public string? Label { get; set; }
    public string ArtifactHash { get; set; } = "";
    public string FileName { get; set; } = "";
    public string FilePath { get; set; } = "";
    /// <summary>Transfer content hash (FileTransferModule ContentHash) of the package file.</summary>
    public string ContentHash { get; set; } = "";
    public long SizeBytes { get; set; }
    public int FileCount { get; set; }
    public bool Hidden { get; set; }
    public DateTime PublishedUtc { get; set; }
}

/// <summary>Where an installed build came from (kept for the future Registry and for audits).</summary>
public sealed class InstallRecord
{
    public string ContentHash { get; set; } = "";
    public string FileName { get; set; } = "";
    public List<string> HostNodeIds { get; set; } = new();
    public string ScriptId { get; set; } = "";
    public string Version { get; set; } = "";
    public string ArtifactHash { get; set; } = "";
    public int? ScriptIndexId { get; set; }
    public string? InstalledFolder { get; set; }
    public bool Replaced { get; set; }
    public DateTime InstalledUtc { get; set; }
}

/// <summary>An installed build as shown on the publish side of the page.</summary>
public sealed class PublishableBuild
{
    public int ScriptIndexId { get; set; }
    public string ScriptId { get; set; } = "";
    public string Version { get; set; } = "";
    public string? Label { get; set; }
    public string ArtifactHash { get; set; } = "";
    public bool IsAvailable { get; set; }
    public bool HashMatch { get; set; }
    /// <summary>Why it can't be published, if it can't.</summary>
    public string? Blocker { get; set; }
    public PublishedPackage? Published { get; set; }
    /// <summary>The published package is still tracked and seeded by FileTransferModule.</summary>
    public bool Seeding { get; set; }
    /// <summary>Came from another node (see <see cref="InstallRecord"/>).</summary>
    public InstallRecord? Origin { get; set; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RemotePackageStatus
{
    /// <summary>No build of this script here.</summary>
    New,
    /// <summary>Other versions of this script are installed.</summary>
    NewVersion,
    /// <summary>Exactly this build is installed.</summary>
    Installed,
    /// <summary>Same script and version installed, but different files (different artifact hash).</summary>
    Conflict
}

/// <summary>A script package seen in the file catalogue.</summary>
public sealed class RemoteScriptPackage
{
    public string ContentHash { get; set; } = "";
    public string FileName { get; set; } = "";
    public string ScriptId { get; set; } = "";
    public string Version { get; set; } = "";
    public string HashPrefix { get; set; } = "";
    public long SizeBytes { get; set; }
    public List<string> HostNodeIds { get; set; } = new();
    public bool PublishedHere { get; set; }
    public DateTime LastSeenUtc { get; set; }
    public RemotePackageStatus Status { get; set; }
    /// <summary>Installed build id for Installed/Conflict.</summary>
    public int? LocalBuildId { get; set; }
    public string? LocalArtifactHash { get; set; }
    /// <summary>Install task currently running for this package, if any.</summary>
    public string? ActiveTaskId { get; set; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum InstallTaskState
{
    Queued,
    FetchingManifest,
    Downloading,
    Verifying,
    Installing,
    Installed,
    AlreadyInstalled,
    Conflict,
    Failed
}

public sealed class InstallTask
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string ContentHash { get; set; } = "";
    public string FileName { get; set; } = "";
    public List<string> HostNodeIds { get; set; } = new();
    public bool Replace { get; set; }
    public InstallTaskState State { get; set; } = InstallTaskState.Queued;
    public double Progress { get; set; }
    public string Message { get; set; } = "";
    /// <summary>Validation errors/warnings from the installer, if it got that far.</summary>
    public List<string> Report { get; set; } = new();
    public string? ScriptId { get; set; }
    public string? Version { get; set; }
    public string? ArtifactHash { get; set; }
    public string? InstalledArtifactHash { get; set; }
    public int? ScriptIndexId { get; set; }
    public string? DownloadTaskId { get; set; }
    public DateTime StartedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? FinishedUtc { get; set; }

    public bool IsFinished => State is InstallTaskState.Installed or InstallTaskState.AlreadyInstalled
        or InstallTaskState.Conflict or InstallTaskState.Failed;
}
