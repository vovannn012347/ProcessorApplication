namespace Common.Interfaces.Consensus;

/// <summary>
/// Outcome of comparing a locally-held record against one received from a peer during
/// consensus merge. Channel implementations decide this using whatever conflict-resolution
/// rule makes sense for their data (trust-weighted average, latest-timestamp-wins,
/// highest-version-wins, etc).
/// </summary>
public enum ConsensusMergeResolution
{
    /// <summary>Keep the record we already have; discard the incoming one.</summary>
    KeepExisting,
    /// <summary>Accept the incoming record, replacing (or creating) our local copy.</summary>
    AcceptIncoming,
    /// <summary>Reject the incoming record entirely (e.g. failed validation) without affecting our local copy.</summary>
    Reject
}

/// <summary>
/// Contract implemented by any module that wants its own data to participate in the P2P
/// epidemic consensus mesh as an independent channel, separate from every other channel's
/// convergence run.
///
/// A "record" here is whatever atomic unit of data this channel synchronizes - a known peer's
/// trust data, a file catalog entry, a shared script's metadata, etc. Each channel owns its
/// own record id space and its own conflict-resolution rules; the consensus layer only needs
/// to move opaque signed bytes around and ask this interface how to resolve conflicts.
///
/// Register an implementation with `services.AddSingleton&lt;IConsensusChannel, YourChannel&gt;()`
/// in your module's ConfigureServices - the P2P module's ConsensusChannelRegistry picks up every
/// registered IConsensusChannel across all modules automatically via DI, no other wiring needed.
///
/// NOTE: ChannelId/DisplayName/SchemaVersion are gossiped as part of node identity so peers know
/// what channels each other support. The record-level methods are called live by
/// GenericChannelSyncService for any channel other than the built-in peer-trust one (which still
/// runs its own dedicated daily windowed signed epidemic pipeline, untouched by this contract).
/// </summary>
public interface IConsensusChannel
{
    /// <summary>
    /// Stable, globally-unique identifier for this channel. Convention: "&lt;module-id-lowercase&gt;.&lt;purpose&gt;",
    /// e.g. "p2p.peer-trust", "filetransfer.catalog", "scriptprovisioning.catalog".
    /// This id is what gets gossiped in a node's identity and used to key sessions/messages once
    /// the protocol is threaded with channel ids.
    /// </summary>
    string ChannelId { get; }

    /// <summary>Human-readable name for admin UI / diagnostics.</summary>
    string DisplayName { get; }

    /// <summary>
    /// Schema/payload version for this channel. Bump when the serialized record shape changes so
    /// peers can detect a mismatch and skip attempting to merge incompatible payloads instead of
    /// failing to parse them.
    /// </summary>
    int SchemaVersion { get; }

    /// <summary>
    /// Ids of records this channel currently holds locally and is willing to advertise/sync.
    /// </summary>
    Task<IReadOnlyCollection<string>> GetLocalRecordIdsAsync(CancellationToken ct = default);

    /// <summary>
    /// Raw serialized bytes for a single record, ready to sign and transmit to a peer.
    /// Returns null if the record no longer exists locally.
    /// </summary>
    Task<byte[]?> GetRecordPayloadAsync(string recordId, CancellationToken ct = default);

    /// <summary>
    /// Decide how to resolve a record received from a peer against whatever we hold locally
    /// (existingPayload is null if we don't have this record yet).
    /// </summary>
    Task<ConsensusMergeResolution> ResolveConflictAsync(string recordId, byte[] incomingPayload, byte[]? existingPayload, CancellationToken ct = default);

    /// <summary>
    /// Commit a record accepted via ResolveConflictAsync into this channel's own backing store.
    /// </summary>
    Task ApplyRecordAsync(string recordId, byte[] payload, CancellationToken ct = default);

    /// <summary>
    /// Called with record ids a peer reported as present that we ALSO already have locally (the
    /// complement of what ResolveConflictAsync/ApplyRecordAsync handle - those cover records we
    /// don't have yet; this covers ones we do). Lets a channel refresh "still confirmed present"
    /// bookkeeping (e.g. a LastSeenUtc) without a full payload re-fetch, so it can evict entries
    /// nobody has confirmed presence of in a while. A no-op default is fine for channels that
    /// don't need aging/eviction.
    /// </summary>
    Task MarkRecordsStillPresentAsync(IEnumerable<string> recordIds, CancellationToken ct = default);

    /// <summary>
    /// Given ids a peer has that we don't, return the subset actually worth fetching. Runs BEFORE
    /// any payload round-trip, so a channel can apply admission limits (e.g. a per-host cap) from
    /// the ids alone instead of fetching payloads it will only reject. Returning the input
    /// unchanged is always valid.
    /// </summary>
    Task<IReadOnlyList<string>> SelectRecordsToFetchAsync(IReadOnlyList<string> candidateRecordIds, CancellationToken ct = default);
}
