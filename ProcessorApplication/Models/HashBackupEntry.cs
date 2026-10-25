using ProcessorApplication.Database.Models;

namespace ProcessorApplication.Models;

/// <summary>
/// Backup of one server key chain link, written as a JSON file per stamp.
/// Never contains a plaintext key. Together with the node root key file it is enough to rebuild
/// the whole key chain: derived keys are recomputed from <see cref="StampTimeText"/>, legacy keys are
/// unwrapped from <see cref="WrappedKey"/>, and <see cref="KeyCheck"/> confirms each recovered key.
/// </summary>
public class HashBackupEntry
{
    public int FormatVersion { get; set; } = 2;

    public int Id { get; set; }

    public DateTime StampTime { get; set; }

    /// <summary>Exact stamp time as used for key derivation (yyyy-MM-ddTHH:mm:ss.fffZ).</summary>
    public string StampTimeText { get; set; } = "";

    public StampKeyMode KeyMode { get; set; }

    /// <summary>SHA-256 of the key (hex). Not the key.</summary>
    public string KeyCheck { get; set; } = "";

    /// <summary>Legacy keys only: the key encrypted with the node root key.</summary>
    public string? WrappedKey { get; set; }

    /// <summary>Hash of this link's content (current format); the next link's PreviousHash for new stamps.</summary>
    public string BlockHash { get; set; } = "";

    public string PreviousBlockHash { get; set; } = "";

    /// <summary>Node root key fingerprint the entry belongs to.</summary>
    public string NodeKeyId { get; set; } = "";
}
