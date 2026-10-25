using System.ComponentModel.DataAnnotations;

namespace ProcessorApplication.Configuration.Settings;

public class SecuritySettings
{
    //public const string SectionName = "SecuritySettings";

    // TimeSpan string format (e.g., "24:00:00")
    [Required(ErrorMessage = "Hash Period is required.")]
    [Display(Name = "Hash Generation Period (in hours)")]
    [Range(0.01, 8760.0, ErrorMessage = "Period must be between 0.01 and 8760 hours.")]
    public double HashStampGenerationPeriod { get; set; } = 24;

    [Display(Name = "Generate the hash stamp prior to certain ")]
    public double HashStampLeadingHours { get; set; } = 1;
    /// <summary>
    /// Also copy the stamp backups and the node root key to <see cref="HashStampBackupFilePath"/>
    /// (a safer place: another disk, a USB stick, a network share). The local backup is always written.
    /// </summary>
    [Display(Name = "Enable hash stamp backup")]
    public bool HashStampBackupEnabled { get; set; } = false;
    [Display(Name = "Forensic backup directory")]
    public string HashStampBackupFilePath { get; set; } = "";

    /// <summary>Node root key file. Empty = Module_Data/Main/keys/node.key next to the application.</summary>
    [Display(Name = "Node root key file")]
    public string NodeKeyFilePath { get; set; } = "";

    /// <summary>Local stamp backup folder (always written). Empty = Module_Data/Main/hashstamps next to the application.</summary>
    [Display(Name = "Local hash stamp backup directory")]
    public string LocalStampBackupPath { get; set; } = "";
    [Display(Name = "Sensitive data access Logging")]
    public bool RecordDecipherLogging { get; set; } = false;
    [Display(Name = "Decipher Log Output Path")]
    public string RecordDecipherLogPath { get; set; } = "";
}