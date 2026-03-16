using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

using Microsoft.EntityFrameworkCore;

namespace ProcessorModule.Database.Models;

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

    [Required]
    public string ScriptId { get; set; }


    public string? ResultMessage { get; set; }

    //step subdirectory
    public string StepDirectoryName { get; set; }
}