using System.Text.Json;

using Common.Interfaces.Consensus;

using P2PModule.Models;
using P2PModule.Services.Persistence;

namespace P2PModule.Services.Maintenance;

/// <summary>
/// Wraps the P2P module's existing built-in peer-trust data (PeerNodeHistoric records tracked by
/// IPeerTracker) as a named, advertised consensus channel.
///
/// IMPORTANT: this makes the "p2p.peer-trust" channel show up in ConsensusChannelRegistry and
/// therefore in gossiped node identity, so peers and admin tooling can see it like any other
/// channel. It does NOT change how peer-trust consensus actually runs today - ConsensusProtocolHandler /
/// ConsensusSessionManager / ConsensusMergeEngine still run their own hardcoded pass independent of
/// this class. ResolveConflictAsync/ApplyRecordAsync below are not called by the live pipeline yet;
/// wiring the real merge engine through IConsensusChannel (so this class's logic actually drives
/// peer-trust convergence, and other modules' channels run their own independent passes) is the
/// deeper protocol refactor - not part of this foundation step, done to avoid touching the current
/// stable consensus behavior.
/// </summary>
public class PeerTrustConsensusChannel : IConsensusChannel
{
    public const string CHANNEL_ID = "p2p.peer-trust";

    public string ChannelId => CHANNEL_ID;
    public string DisplayName => "Peer Trust & Rating";
    public int SchemaVersion => 1;

    private readonly IPeerTracker _peerTracker;

    public PeerTrustConsensusChannel(IPeerTracker peerTracker)
    {
        _peerTracker = peerTracker;
    }

    public Task<IReadOnlyCollection<string>> GetLocalRecordIdsAsync(CancellationToken ct = default)
    {
        IReadOnlyCollection<string> ids = _peerTracker
            .GetLastSeenNodes(PeerTracker.DefaultNodeComparer)
            .Select(p => p.Identity.NodeId)
            .Where(id => !string.IsNullOrEmpty(id))
            .ToList();

        return Task.FromResult(ids);
    }

    public Task<byte[]?> GetRecordPayloadAsync(string recordId, CancellationToken ct = default)
    {
        var peer = _peerTracker.GetPeer(recordId);
        if (peer == null)
            return Task.FromResult<byte[]?>(null);

        return Task.FromResult<byte[]?>(JsonSerializer.SerializeToUtf8Bytes<PeerNodeHistoric>(peer));
    }

    public Task<ConsensusMergeResolution> ResolveConflictAsync(string recordId, byte[] incomingPayload, byte[]? existingPayload, CancellationToken ct = default)
    {
        // Not yet invoked by the live pipeline - see class remarks. Real trust-weighted merge
        // rules live in ConsensusMergeEngine/ConsensusProtocolHandler today.
        return Task.FromResult(ConsensusMergeResolution.Reject);
    }

    public Task ApplyRecordAsync(string recordId, byte[] payload, CancellationToken ct = default)
    {
        // Not yet invoked by the live pipeline - see class remarks.
        return Task.CompletedTask;
    }

    public Task MarkRecordsStillPresentAsync(IEnumerable<string> recordIds, CancellationToken ct = default)
    {
        // Not yet invoked by the live pipeline - see class remarks.
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> SelectRecordsToFetchAsync(IReadOnlyList<string> candidateRecordIds, CancellationToken ct = default)
    {
        // Not yet invoked by the live pipeline - see class remarks.
        return Task.FromResult(candidateRecordIds);
    }
}
