using System.ComponentModel.DataAnnotations;

namespace PortabilityModule.Models;

public class DataPortabilitySettings
{
    [Display(Name = "Temporary Storage Path")]
    [Required]
    public string TempStoragePath { get; set; } = "./temp/exports";

    [Display(Name = "Archive Retention (Minutes)")]
    public int ArchiveRetentionMinutes { get; set; } = 60;
}
