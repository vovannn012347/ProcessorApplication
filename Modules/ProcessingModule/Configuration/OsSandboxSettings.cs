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
    /// </summary>
    [Required]
    [Display(Name = "Processing User Name")]
    public string UserName { get; set; } = "Processor-User";

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
}
