using System.ComponentModel.DataAnnotations;

namespace ProcessorApplication.Models.Settings;

public class SecuritySettings
{
    public const string SectionName = "SecuritySettings";

    // TimeSpan string format (e.g., "24:00:00")
    [Required(ErrorMessage = "Hash Period is required.")]
    [Display(Name = "Hash Generation Period (in hours)")]
    [Range(0.01, 8760.0, ErrorMessage = "Period must be between 0.01 and 8760 hours.")]
    public double HashStampGenerationPeriod { get; set; } = 24;
    [Display(Name = "Enable hash stamp backup")]
    public bool HashStampBackupEnabled { get; set; } = false;
    [Display(Name = "Forensic backup directory")]
    public string HashStampBackupFilePath { get; set; } = "";
    [Display(Name = "Sensitive data access Logging")]
    public bool RecordDecipherLogging { get; set; } = false;
    [Display(Name = "Decipher Log Output Path")]
    public string RecordDecipherLogPath { get; set; } = "";
}