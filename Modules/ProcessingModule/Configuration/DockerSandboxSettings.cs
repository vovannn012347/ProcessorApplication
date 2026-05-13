using System.ComponentModel.DataAnnotations;

namespace ProcessingModule.Configuration;

public class DockerSandboxSettings
{
    public const string SectionName = "DockerProcessingSettings";

    /// <summary>
    /// Docker image used for processing (e.g., lightweight Python + Torch image).
    /// </summary>
    [Required]
    [Display(Name = "Docker Image Name")]
    public string ImageName { get; set; } = "ml-processor-sandbox:latest";

    /// <summary>
    /// Maximum allowed container runtime before force termination.
    /// </summary>
    [Range(1, 3600)]
    [Display(Name = "Container Timeout (Seconds)")]
    public int ContainerTimeoutSeconds { get; set; } = 600;

    /// <summary>
    /// Maximum memory limit for the container in megabytes.
    /// </summary>
    [Range(128, 65536)]
    [Display(Name = "Memory Limit (MB)")]
    public int MemoryLimitMb { get; set; } = 2048;

    /// <summary>
    /// Relative CPU share weight for the container.
    /// </summary>
    [Range(2, 262144)]
    [Display(Name = "CPU Shares")]
    public int CpuShares { get; set; } = 512;

    /// <summary>
    /// Docker network mode. Should typically be 'none' for medical / secure workloads.
    /// </summary>
    [Required]
    [Display(Name = "Network Mode")]
    public DockerNetworkMode NetworkMode { get; set; } = DockerNetworkMode.None;

    /// <summary>
    /// Mount root filesystem as read-only inside the container.
    /// </summary>
    [Display(Name = "Read-Only Root Filesystem")]
    public bool ReadOnlyRootFs { get; set; } = true;

    /// <summary>
    /// Explicit bind mounts in the format: host:container:ro|rw
    /// Keep minimal and explicitly controlled.
    /// </summary>
    [Display(Name = "Volume Bindings")]
    public List<string> Volumes { get; set; } = new();

    /// <summary>
    /// User and group ID under which the container runs (non-root).
    /// </summary>
    [Required]
    [Display(Name = "Container User")]
    public string User { get; set; } = "1000:1000";
}

public enum DockerNetworkMode
{
    None,
    Bridge,
    Host
}
