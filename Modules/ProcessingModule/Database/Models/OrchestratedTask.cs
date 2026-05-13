using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

using Microsoft.EntityFrameworkCore;

namespace ProcessingModule.Database.Models;

// Execution Record
public class OrchestratedTask
{
    [Key]
    public Guid Id { get; set; }

    [Required]
    public string Status { get; set; } = "Pending"; // "Pending", "Queued", "Running", "Complete", "Error"

    public string? ResultHash { get; set; } // May be null if only sub-jobs are hashed

    [Required]
    public DateTime CreatedTime { get; set; } = DateTime.UtcNow;
    [Required]
    public DateTime CompletedTime { get; set; } = DateTime.UtcNow;

    [Required]
    public string InitiatorUserId { get; set; } = "Unknown";

    //task directory
    [Required]
    public string PhysicalPathRoot { get; set; } = "";

    public virtual ICollection<OrchestratedTaskSubJob> SubJobs { get; set; } = new List<OrchestratedTaskSubJob>();
}

