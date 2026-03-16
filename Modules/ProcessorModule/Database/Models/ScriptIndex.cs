using System.ComponentModel.DataAnnotations;

using Microsoft.EntityFrameworkCore;

namespace ProcessorModule.Database.Models;

public class ScriptIndex
{
    [Key]
    public int Id { get; set; }

    /// <summary>
    /// Corresponds to 'script_id' in manifest (e.g., glaucoma-retinal-analyzer)
    /// </summary>
    [Required]
    public string ScriptIdentifier { get; set; }

    /// <summary>
    /// Corresponds to 'script_label' (The localized/friendly name)
    /// </summary>
    public string ScriptLabel { get; set; }

    /// <summary>
    /// Corresponds to 'version' (The script's own version)
    /// </summary>
    public string ScriptVersion { get; set; }

    /// <summary>
    /// Corresponds to 'processor_version' (The version of the engine it targets)
    /// </summary>
    public string ProcessorVersion { get; set; }

    /// <summary>
    /// Corresponds to 'artifact_hash_id' (Generated upon download/deployment)
    /// </summary>
    public string ArtifactHash { get; set; }
    public bool HashMatch { get; set; }

    /// <summary>
    /// The physical path to the directory containing the manifest and code
    /// </summary>
    [Required]
    public string ManifestDirectoryPath { get; set; }

    public bool IsAvailable { get; set; }

    /// <summary>
    /// script indexing time in the library
    /// </summary>
    public DateTime CreatedTime { get; set; } = DateTime.UtcNow;
}