namespace FileTransferModule.Models;

/// <summary>
/// Per-peer reputation for file spreading only. Deliberately separate from P2PModule's
/// IRatingService (the general node reputation): a peer serving a corrupt block says something
/// about it as a file source, not about its routing/consensus behaviour, so nothing here feeds
/// back into the P2P rating.
///
/// All counters are about data THIS node received FROM the peer while downloading.
/// </summary>
public class FilePeerReputation
{
    public string NodeId { get; set; } = string.Empty;

    /// <summary>Blocks that passed SHA-256 verification to which this peer contributed at least one slice.</summary>
    public long BlocksVerified { get; set; }

    /// <summary>Bytes received from this peer that ended up in verified blocks.</summary>
    public long BytesVerified { get; set; }

    /// <summary>
    /// Blocks that failed verification where this peer supplied EVERY slice - i.e. the bad data is
    /// provably theirs. This is what bans a peer.
    /// </summary>
    public int BadBlocksSoleSource { get; set; }

    /// <summary>
    /// Blocks that failed verification where this peer was one of several contributors - suspicious
    /// but not proof. The failing block is then retried from a single peer to find the culprit.
    /// </summary>
    public int BadBlocksSharedSource { get; set; }

    /// <summary>Slice requests the peer accepted (channel open) but never answered in time.</summary>
    public long SliceTimeouts { get; set; }

    /// <summary>Sole-source bad blocks since the last ban/reset; reaching the configured threshold bans the peer.</summary>
    public int BadBlocksSinceLastBan { get; set; }

    public DateTime? BannedUntilUtc { get; set; }
    public DateTime FirstSeenUtc { get; set; } = DateTime.UtcNow;
    public DateTime LastUpdatedUtc { get; set; } = DateTime.UtcNow;

    public bool IsBanned(DateTime nowUtc) => BannedUntilUtc.HasValue && BannedUntilUtc.Value > nowUtc;

    /// <summary>
    /// Ordering score used to pick which seeders to connect to / request from first. Unknown peers
    /// sit at 0; good history raises it slowly, provable corruption drops it hard.
    /// </summary>
    public double Score =>
        BlocksVerified
        - BadBlocksSoleSource * 25.0
        - BadBlocksSharedSource * 2.0
        - SliceTimeouts * 0.2;
}
