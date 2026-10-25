using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

using Microsoft.EntityFrameworkCore;

namespace ProcessingModule.Database.Models;

// Execution Record
public class OrchestratedTaskSubJob
{
    [Key]
    public Guid Id { get; set; } // This corresponds to 'processing_id' in the manifest

    [Required]
    public Guid ParentJobId { get; set; }

    [ForeignKey(nameof(ParentJobId))]
    public virtual OrchestratedTask ParentJob { get; set; }

    [Required]
    public string Status { get; set; } // "Pending", "Queued", "Running", "Complete", "Error"

    public string? ResultHash { get; set; } // Calculated after script completion

    [Required]
    public DateTime CreatedTime { get; set; } = DateTime.UtcNow;
    [Required]
    public DateTime CompletedTime { get; set; } = DateTime.UtcNow;

    [Required]
    public int Sequence { get; set; }

    /// <summary>script_id from the manifest. Kept for display and for steps created before builds were tracked.</summary>
    [Required]
    public string ScriptId { get; set; }

    /// <summary>Version of the script build this step runs (manifest "version").</summary>
    public string? ScriptVersion { get; set; }

    /// <summary>
    /// The exact library entry (build) this step runs: <see cref="ScriptIndex.Id"/>.
    /// Null only for steps created before builds were tracked; those fall back to <see cref="ScriptId"/>.
    /// No foreign key on purpose: removing a build from the library must not touch job history.
    /// </summary>
    public int? ScriptIndexId { get; set; }

    /// <summary>
    /// Steps (sub-job IDs, comma-separated) that must complete before this one may start.
    /// Empty for batch jobs: their scripts are independent and run whenever a slot is free.
    /// In an orchestration only the dependents of a failed step are skipped; independent steps still run.
    /// </summary>
    public string? DependsOn { get; set; }

    [NotMapped]
    public IReadOnlyList<Guid> DependsOnIds =>
        string.IsNullOrWhiteSpace(DependsOn)
            ? Array.Empty<Guid>()
            : DependsOn.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(x => Guid.TryParse(x, out var g) ? g : Guid.Empty)
                .Where(g => g != Guid.Empty)
                .ToList();


    public string? ResultMessage { get; set; }

    //step subdirectory
    public string StepDirectoryName { get; set; }
}