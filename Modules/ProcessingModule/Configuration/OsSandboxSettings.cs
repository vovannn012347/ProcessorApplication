using System.ComponentModel.DataAnnotations;

namespace ProcessingModule.Configuration;
public class OsSandboxSettings
{
    public const string SectionName = "OsConatinedProcessingSettings";


    /// <summary>
    /// If true, attempts to create the OS user on application startup.
    /// Requires elevated privileges or user permission
    /// </summary>
    //[Display(Name = "Auto-Create Processing User")]
    //public bool AutoCreateProcessingUser { get; set; } = false;

    /// <summary>
    /// OS-level user used to run processing jobs.
    /// Windows adds "-rights" for the supervisor account and local account names are limited to 20 characters, so the
    /// name may have at most 13. (Linux lower-cases it and uses no second account.)
    /// </summary>
    [Required]
    [StringLength(13, ErrorMessage = "The processing user name may have at most 13 characters (Windows adds \"-rights\" for a second account, and account names are limited to 20).")]
    [RegularExpression(@"^[A-Za-z][A-Za-z0-9_-]*$", ErrorMessage = "Use letters, digits, '-' and '_' only, starting with a letter.")]
    [Display(Name = "Processing User Name")]
    public string UserName { get; set; } = "Pr-User";

    /// <summary>
    /// Optional OS group for shared access (e.g., file permissions).
    /// </summary>
    [Display(Name = "Processing Group Name")]
    public string? GroupName { get; set; } = "ml-processor";

    /// <summary>
    /// The randomly generated password for the restricted user.
    /// This is managed automatically and should not be edited manually.
    /// </summary>
    public string? UserPassword { get; set; }
    /// <summary>
    /// The randomly generated password for the restricting user.
    /// This is managed automatically and should not be edited manually.
    /// </summary>
    public string? RightsUserPassword { get; set; }

    /// <summary>
    /// Home directory for the processing user.
    /// Null indicates no home directory (system-user style).
    /// </summary>
    //[Display(Name = "Home Directory")]
    //public string? HomeDirectory { get; set; } = null;

    /// <summary>
    /// Login shell for the processing user.
    /// Linux only; ignored on Windows.
    /// </summary>
    [Display(Name = "User Shell")]
    public string Shell { get; set; } = "/usr/sbin/nologin";

    /// <summary>
    /// Let scripts use the network. Off (default): Provision blocks all network traffic of the worker account
    /// (Linux: iptables by user id, loopback included; Windows: firewall rules for the worker's user, loopback is not
    /// filtered by Windows). Re-run Provision after changing it.
    /// </summary>
    [Display(Name = "Allow Network")]
    public bool AllowNetwork { get; set; } = false;

    /// <summary>
    /// Windows: Setup User denies the worker read access to the node home (databases, node.key, settings). The script,
    /// results and executor folders keep their own explicit grants, which outrank the inherited deny even inside the node home.
    /// Re-run Provision after changing it.
    /// </summary>
    [Display(Name = "Protect Node Home")]
    public bool ProtectNodeHome { get; set; } = true;

    /// <summary>Memory limit for one processing step (all its processes together), in MB. 0 = no limit. Windows only for now.</summary>
    [Range(0, 1048576)]
    [Display(Name = "Memory Limit (MB)")]
    public int MemoryLimitMb { get; set; } = 0;

    /// <summary>CPU limit for one processing step, in percent of the whole machine (1-100). 0 = no limit. Windows only for now.</summary>
    [Range(0, 100)]
    [Display(Name = "CPU Limit (%)")]
    public int CpuLimitPercent { get; set; } = 0;
}
