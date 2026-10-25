using System.ComponentModel.DataAnnotations;

using ProcessingModule.Services.Runtime;

namespace ProcessingModule.Configuration;
public class ProcessorSettings
{

    [Required]
    [Display(Name = "Script Source Directory")]
    // Where the scripts physically reside on disk
    public string ScriptSourcePath { get; set; } = "./Module_Data/Processor/scripts";

    [Required]
    [Display(Name = "Script Processing Directory")]
    // Where result files will be written
    public string ResultsOutputPath { get; set; } = "./Module_Data/Processor/processing";

    /// <summary>
    /// How many scripts may run at the same time on this node, across all jobs.
    /// Each running script is one executor process; keep this low on weak machines.
    /// (Name kept for saved settings compatibility.)
    /// </summary>
    [Display(Name = "Scripts Running At Once")]
    [Range(1, 64)]
    public int MaxConcurrentJobs { get; set; } = 1;

    [Display(Name = "Job Timeout (Minutes)")]
    // Timeout if job is processing above this time
    public double JobTimeoutMinutes { get; set; } = 5;

    /// <summary>
    /// Processing mechanism - the tool used for script running
    /// Expected values: "python", other values WIP
    /// </summary>
    [Required]
    [Display(Name = "Processing Type")]
    public ProcessingType ProcessingType { get; set; } = ProcessingType.Python;

    /// <summary>
    /// Sandboxing mechanism used for processing.
    /// Expected values: "OS", "Docker"
    /// </summary>
    [Required]
    [Display(Name = "Sandboxing Type")]
    public SandboxType SandboxingType { get; set; } = SandboxType.OSUser;

    /// <summary>
    /// If true, application startup fails when sandboxing is not none and is unavailable.
    /// </summary>
    [Display(Name = "Require Sandboxing")]
    public bool RequireSandboxing { get; set; } = true;

    /// <summary>
    /// Perform sandbox readiness validation on application startup.
    /// </summary>
    [Display(Name = "Validate Sandbox On Startup")]
    public bool ValidateSandboxOnStartup { get; set; } = true;

    /// <summary>
    /// Perform sandbox readiness validation on application startup.
    /// </summary>
    [Display(Name = "Update Script Hash On Mismatch")]
    public bool UpdateHashOnMismatch { get; set; } = true;

    // ---- Script package upload (admin) ----

    /// <summary>Largest .zip the admin upload accepts.</summary>
    [Display(Name = "Max Script Upload (MB)")]
    [Range(1, 65536)]
    public int MaxScriptUploadMb { get; set; } = 2048;

    /// <summary>Total size a package may extract to (bytes actually written are counted).</summary>
    [Display(Name = "Max Extracted Package Size (MB)")]
    [Range(1, 131072)]
    public int MaxScriptExtractedMb { get; set; } = 4096;

    /// <summary>Largest single file inside a package.</summary>
    [Display(Name = "Max Single File In Package (MB)")]
    [Range(1, 131072)]
    public int MaxScriptEntryMb { get; set; } = 2048;

    /// <summary>Most files and folders a package may contain.</summary>
    [Display(Name = "Max Files In Package")]
    [Range(1, 1000000)]
    public int MaxScriptArchiveEntries { get; set; } = 20000;

    /// <summary>
    /// Folder for the result chain head backups (last sequence per user). Empty = Module_Data/Processor/result_chain
    /// next to the application. Put it somewhere the database copy doesn't go, so a truncated chain can be noticed.
    /// </summary>
    [Display(Name = "Result Chain Head Backup Directory")]
    public string ResultChainBackupPath { get; set; } = "";

    /// <summary>Largest allowed expansion of one file (extracted / compressed). Normal code compresses 3-10x.</summary>
    [Display(Name = "Max Compression Ratio")]
    [Range(2, 10000)]
    public int MaxScriptCompressionRatio { get; set; } = 100;
}
