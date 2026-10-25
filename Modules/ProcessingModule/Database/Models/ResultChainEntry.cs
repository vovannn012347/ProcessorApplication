using System.ComponentModel.DataAnnotations;

namespace ProcessingModule.Database.Models;

/// <summary>
/// One link of a user's result hash chain: the record that a script build produced these outputs from these inputs.
///
/// The record itself is stored in clear (no data encryption: too costly on weak machines).
/// <see cref="Seal"/> holds the hash of this entry and the hash of the previous entry, encrypted with a key derived
/// from the owner's personal key and the server key of that period. Without that key nobody can write a valid seal,
/// so editing a record, reordering or removing entries in the middle is detected on verification; removing entries
/// from the end is detected through the external head backup.
/// </summary>
public class ResultChainEntry
{
    [Key]
    public long Id { get; set; }

    /// <summary>User the results belong to (today the job initiator).</summary>
    [Required]
    public string OwnerId { get; set; } = "";

    /// <summary>Position in the owner's chain, starting at 1, no gaps.</summary>
    public long Sequence { get; set; }

    public DateTime CreatedTime { get; set; }

    public Guid JobId { get; set; }
    public Guid StepId { get; set; }

    [Required]
    public string ScriptId { get; set; } = "";
    public string? ScriptVersion { get; set; }
    public int? ScriptBuildId { get; set; }
    public string? ArtifactHash { get; set; }

    [Required]
    public string Status { get; set; } = "";

    /// <summary>Canonical JSON: path, size and SHA-256 of every input and output file.</summary>
    [Required]
    public string PayloadJson { get; set; } = "";

    /// <summary>SHA-256 of all fields above (hex). Recomputed on verification.</summary>
    [Required]
    [StringLength(64)]
    public string ContentHash { get; set; } = "";

    /// <summary>Server key stamp the seal key was derived from.</summary>
    public int StampId { get; set; }

    /// <summary>True if the seal key is bound to a registered user's personal key.</summary>
    public bool UserBound { get; set; }

    /// <summary>AES-GCM( entry hash | previous entry hash ), base64 of nonce|ciphertext|tag.</summary>
    [Required]
    public string Seal { get; set; } = "";
}
