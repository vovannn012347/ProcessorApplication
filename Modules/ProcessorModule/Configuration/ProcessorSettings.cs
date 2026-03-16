using System.ComponentModel.DataAnnotations;

using ProcessorModule.Services.Runtime;

namespace ProcessorModule.Configuration;
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

    [Display(Name = "Concurrent Worker Limit")]
    [Range(1, 64)]
    public int MaxConcurrentJobs { get; set; } = 4;

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
}
