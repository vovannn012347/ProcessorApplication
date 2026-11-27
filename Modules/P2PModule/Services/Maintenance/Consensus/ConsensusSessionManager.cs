// [full-implementation]
using System.Collections.Concurrent;
using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Common.Interfaces;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using P2PModule.Configuration;
using P2PModule.Infrastructure;
using P2PModule.Interfaces;
using P2PModule.Models;
using P2PModule.Models.Gossips;
using P2PModule.Models.Network;
using P2PModule.Services.Connection;
using P2PModule.Services.Gossip;
using P2PModule.Services.Persistence;

namespace P2PModule.Services.Maintenance;

/// <summary>
/// Metadata container representing a sharded delta artifact folder within a consensus session.
/// Deltas live inside generated_deltas or downloaded_deltas alongside IterDir and MergeDir.
/// </summary>
public class ConsensusSessionDeltaItem
{
    public string DeltaId { get; set; } = string.Empty;
    /// <summary>Chain signers we had no key for when the delta arrived (taken in on trust, not counted as visited).</summary>
    public List<string> UnconfirmedSigners { get; set; } = new();
    public string SourceNodeId { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Path to the root directory for this delta: {StagingOperation.BasePath}/generated_deltas/{DeltaId}
    /// or {StagingOperation.BasePath}/downloaded_deltas/{DeltaId}
    /// </summary>
    public string DeltaFolderPath { get; set; } = string.Empty;

    /// <summary>
    /// Asymmetric consensus signature chain attached for verification during delta ingestion.
    /// </summary>
    public List<ChainSignature> SignatureChain { get; set; } = new();

    /// <summary>
    /// Cryptographic signature proof covering generation time and signature chain head.
    /// </summary>
    public byte[] Signature { get; set; } = Array.Empty<byte>();
}

/// <summary>
/// Container representing a cryptographic signature envelope for an exchanged set of sync node IDs.
/// </summary>
public class ChainSignature
{
    public int Order { get; set; }
    public string SignerNodeId { get; set; } = string.Empty;
    public string TargetNodeId { get; set; } = string.Empty;
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
    public byte[] Signature { get; set; } = Array.Empty<byte>();
}

/// <summary>
/// Active state container for a daily epidemic consensus merge session.
/// Snapshots local active daily keys at session start to ensure immutable signing across key rotations.
/// One of these exists per (currently) active consensus channel - see ChannelId.
/// </summary>
/// <summary>Outcome of RegisterDownloadedDeltaAsync. Only InvalidSignature is evidence of tampering.</summary>
public enum DeltaRegistration
{
    Registered,
    InvalidSignature,
    UnverifiableSigner,
    Failed
}

public class ConsensusMergeSession
{
    public Guid SessionId { get; set; } = Guid.NewGuid();
    public string TargetSyncDate { get; set; } = DateTime.UtcNow.ToString("yyyy-MM-dd");
    public string InitiatorNodeId { get; set; } = string.Empty;

    /// <summary>
    /// Id of the IConsensusChannel (see Common.Interfaces.Consensus.IConsensusChannel) this session
    /// belongs to. Defaults to the built-in peer-trust channel so existing single-channel behavior
    /// is unchanged unless a caller explicitly passes a different channelId.
    /// </summary>
    public string ChannelId { get; set; } = PeerTrustConsensusChannel.CHANNEL_ID;

    public string TargetNodeId { get; set; } = string.Empty;
    public string GeneratedDeltasDir { get; set; } = string.Empty;
    public string DownloadedDeltasDir { get; set; } = string.Empty;

    public byte[] DayKeyPrivateKey { get; set; } = Array.Empty<byte>();
    public byte[] DayKeyPublicKey { get; set; } = Array.Empty<byte>();
    public byte[] DayKeySignatureKey { get; set; } = Array.Empty<byte>();
    public byte[] DayKeyVerifyKey { get; set; } = Array.Empty<byte>();

    public HashSet<string> PastSyncSet { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> CurrentSyncSet { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>
    /// Nodes whose updates we hold for this session: we merged with them (either side of the swap),
    /// or a valid delta from them (or signed by them) reached us through other nodes.
    /// Popping a node from the queue, a failed connection or a BUSY answer does NOT make it visited.
    /// </summary>
    public HashSet<string> VisitedNodes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Nodes we stopped trying to reach this session (retries used up). Not visited - just not chased any more.</summary>
    public HashSet<string> GaveUpNodes { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public ConcurrentDictionary<string, ChainSignature> SyncSetSignatures { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public ConcurrentQueue<string> NodesToVisit { get; set; } = new();
    /// <summary>Chain signers whose key/record we did not have when their delta arrived: candidates for a pull.</summary>
    public ConcurrentDictionary<string, byte> UnconfirmedSigners { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Last set of node ids we asked neighbours to confirm, so an unchanged set is not asked again.</summary>
    public string LastCorroborationKey { get; set; } = string.Empty;
    /// <summary>Every node id this session heard of (sync sets, chain signers, delta node lists, pulls): what "complete" means.</summary>
    public ConcurrentDictionary<string, byte> KnownIds { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Missing records: when we last asked for them, and how many rounds we asked neighbours without success.</summary>
    public DateTime LastMissingPullUtc { get; set; } = DateTime.MinValue;
    public int MissingPullRounds { get; set; }
    /// <summary>Echo rounds: when the last one ran, how many in a row found every neighbour identical, and which
    /// (peer, node, version) pulls were already tried (a version the other side keeps rejecting is not pulled again).</summary>
    public DateTime LastEchoUtc { get; set; } = DateTime.MinValue;
    public int QuietEchoRounds { get; set; }
    public DateTime LastSingleAskUtc { get; set; } = DateTime.MinValue;
    public ConcurrentDictionary<string, byte> EchoPullsTried { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Who sent us the deltas/records folded into the current iteration: our next delta does not go back to them.</summary>
    public ConcurrentDictionary<string, byte> IterationSources { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>How often a node was put back into the queue because it was busy (see RequeueNodeForRetry).</summary>
    public Dictionary<string, int> VisitRetries { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public ConcurrentDictionary<string, ConsensusSessionDeltaItem> LocalDeltas { get; set; } = new();
    public ConcurrentDictionary<string, ConsensusSessionDeltaItem> PendingDeltasToMerge { get; set; } = new();
    public ConcurrentDictionary<string, Task> ActiveDeltaDownloads { get; set; } = new();

    public ConsensusMergeOperation? StagingOperation { get; set; }
    public DateTime LastActivityUtc { get; set; } = DateTime.UtcNow;
}

public interface IConsensusSessionManager
{
    /// <summary>
    /// channelId identifies which consensus channel (see Common.Interfaces.Consensus.IConsensusChannel)
    /// this call applies to. Every method defaults to the built-in peer-trust channel, so existing
    /// callers that don't pass channelId keep running exactly one session at a time, same as before -
    /// this is what makes several-channel consensus possible: a different channelId gets its own
    /// completely independent session, acquired/visited/finalized on its own schedule.
    /// </summary>
    /// <summary>Returns the new session, or null if the channel already has one running or starting (BUSY).</summary>
    Task<ConsensusMergeSession?> TryAcquireSessionAsync(string initiatorNodeId, string channelId = PeerTrustConsensusChannel.CHANNEL_ID);
    ConsensusMergeSession? GetActiveSession(string channelId = PeerTrustConsensusChannel.CHANNEL_ID);
    bool IsBusy(string channelId = PeerTrustConsensusChannel.CHANNEL_ID);
    void SetActiveTargetNode(string? targetNodeId, string channelId = PeerTrustConsensusChannel.CHANNEL_ID);
    void QueueNodesToVisit(IEnumerable<string> nodeIds, string channelId = PeerTrustConsensusChannel.CHANNEL_ID);
    bool TryPopNextNodeToVisit(out string? nodeId, string channelId = PeerTrustConsensusChannel.CHANNEL_ID);
    void MarkNodeAsVisited(string nodeId, string channelId = PeerTrustConsensusChannel.CHANNEL_ID);
    /// <summary>Puts a node that answered BUSY back at the end of the queue; false once it used up its retries.</summary>
    bool RequeueNodeForRetry(string nodeId, int maxRetries, string channelId = PeerTrustConsensusChannel.CHANNEL_ID);
    /// <summary>Stop chasing a node for this session without claiming we have its updates.</summary>
    void GiveUpNode(string nodeId, string channelId = PeerTrustConsensusChannel.CHANNEL_ID);
    bool IsNodeVisited(string nodeId, string channelId = PeerTrustConsensusChannel.CHANNEL_ID);
    bool HasNodesToVisit(string channelId = PeerTrustConsensusChannel.CHANNEL_ID);
    void RegisterNodesInCurrentSyncSet(IEnumerable<string> nodeIds, string channelId = PeerTrustConsensusChannel.CHANNEL_ID);
    void AddSyncSetSignature(string signerNodeId, string targetNodeId, byte[] signature, int order = 0, string channelId = PeerTrustConsensusChannel.CHANNEL_ID);
    /// <summary>Stores a chain link exactly as it was signed (its own timestamp - the signature covers it).</summary>
    void AddSyncSetSignature(ChainSignature link, string channelId = PeerTrustConsensusChannel.CHANNEL_ID);
    List<ChainSignature> GetSyncSetSignatures(string channelId = PeerTrustConsensusChannel.CHANNEL_ID);
    List<string> GetPastSyncSetNodes(string channelId = PeerTrustConsensusChannel.CHANNEL_ID);
    Task<ConsensusSessionDeltaItem?> CreateLocalDeltaAsync(List<ChainSignature>? signatureChain = null, string channelId = PeerTrustConsensusChannel.CHANNEL_ID);
    bool PrepareDownloadedDeltaFolder(out string deltaId, out string destFolderPath, string channelId = PeerTrustConsensusChannel.CHANNEL_ID);
    Task<DeltaRegistration> RegisterDownloadedDeltaAsync(string sourceNodeId, string downloadedFolderPath, List<ChainSignature>? signatureChain = null, string channelId = PeerTrustConsensusChannel.CHANNEL_ID);
    List<ConsensusSessionDeltaItem> GetPendingDeltasToMerge(string channelId = PeerTrustConsensusChannel.CHANNEL_ID);
    bool HasPendingDeltas(string channelId = PeerTrustConsensusChannel.CHANNEL_ID);
    List<string> GetUnconfirmedSigners(string deltaId, string channelId = PeerTrustConsensusChannel.CHANNEL_ID);
    bool RemovePendingDelta(string deltaId, bool deleteFolder = false, string channelId = PeerTrustConsensusChannel.CHANNEL_ID);
    ConsensusSessionDeltaItem? GetLocalDelta(string deltaId, string channelId = PeerTrustConsensusChannel.CHANNEL_ID);
    List<string> GetDeltaFolderPathsBatch(List<string> deltaIds, string channelId = PeerTrustConsensusChannel.CHANNEL_ID);
    Task<PeerNodeHistoric?> GetNodeFromDeltaOrStagingHierarchyAsync(string deltaId, string nodeId, string channelId = PeerTrustConsensusChannel.CHANNEL_ID);
    Task FinalizeIterationStepAsync(string channelId = PeerTrustConsensusChannel.CHANNEL_ID);
    Task FinalizeSessionAsync(string channelId = PeerTrustConsensusChannel.CHANNEL_ID);
    Task AbortSessionAsync(string channelId = PeerTrustConsensusChannel.CHANNEL_ID);

    /// <summary>Ids of channels that currently have an active session - for admin/diagnostic visibility.</summary>
    IReadOnlyCollection<string> GetActiveChannelIds();
}


public class ConsensusSessionManager : IConsensusSessionManager
{
    private readonly IConsensusMergeEngine _mergeEngine;
    private readonly INodeIdentityService _identity;
    private readonly IPeerTracker _peerTracker;
    private readonly ILogger<ConsensusSessionManager> _logger;

    private readonly object _sessionLock = new();
    // Keyed by ChannelId. A single lock guards the whole dictionary (same as the single-session lock
    // this replaces) - session churn is low-frequency enough that a shared lock across channels is not
    // a contention concern, and it avoids introducing any new per-channel locking subtleties.
    private readonly Dictionary<string, ConsensusMergeSession> _activeSessionsByChannel = new(StringComparer.OrdinalIgnoreCase);
    // Channels whose session is being created right now (staging workspace in progress) - counts as BUSY.
    private readonly HashSet<string> _reservedChannels = new(StringComparer.OrdinalIgnoreCase);

    public ConsensusSessionManager(
        IConsensusMergeEngine mergeEngine,
        INodeIdentityService identity,
        IPeerTracker peerTracker,
        ILogger<ConsensusSessionManager> logger)
    {
        _mergeEngine = mergeEngine;
        _identity = identity;
        _peerTracker = peerTracker;
        _logger = logger;
    }

    /// <summary>
    /// Acquires the session for a channel, or returns null if one is already running (BUSY).
    /// Async because creating the staging workspace is async: the old synchronous version blocked
    /// on it (.GetAwaiter().GetResult()) while holding _sessionLock. Now the channel is RESERVED
    /// under the lock, the workspace is created with no lock held, and the session is installed
    /// under the lock again - so a concurrent request still sees BUSY the whole time.
    /// </summary>
    public async Task<ConsensusMergeSession?> TryAcquireSessionAsync(string initiatorNodeId, string channelId = PeerTrustConsensusChannel.CHANNEL_ID)
    {
        lock (_sessionLock)
        {
            if ((_activeSessionsByChannel.TryGetValue(channelId, out var existing) && existing != null) || _reservedChannels.Contains(channelId))
            {
                _logger.LogWarning("[Session Manager] Rejecting session request from {Initiator} for channel '{ChannelId}' - a session is already running or starting (BUSY).",
                    initiatorNodeId, channelId);
                return null;
            }
            _reservedChannels.Add(channelId);
        }

        try
        {
            string todayKey = DateTime.UtcNow.ToString("yyyy-MM-dd");
            var stagingOp = await _mergeEngine.CreateStagingOperationAsync(todayKey);

            // The session caches its own copy of the day keys, taken as ONE locked snapshot: the
            // whole session signs/verifies with a matching set even if keys rotate mid-session.
            var dayKeys = _identity.GetDayKeys();

            var newSession = new ConsensusMergeSession
            {
                TargetSyncDate = todayKey,
                InitiatorNodeId = initiatorNodeId,
                ChannelId = channelId,
                LastActivityUtc = DateTime.UtcNow,

                StagingOperation = stagingOp,
                GeneratedDeltasDir = Path.Combine(stagingOp.BasePath, "generated_deltas"),
                DownloadedDeltasDir = Path.Combine(stagingOp.BasePath, "downloaded_deltas"),

                DayKeyPrivateKey = dayKeys.PrivateKey,
                DayKeyPublicKey = dayKeys.PublicKey,
                DayKeySignatureKey = dayKeys.PrivateSignatureKey,
                DayKeyVerifyKey = dayKeys.PublicVerifyKey
            };

            Directory.CreateDirectory(newSession.GeneratedDeltasDir);
            Directory.CreateDirectory(newSession.DownloadedDeltasDir);

            newSession.PastSyncSet.Add(dayKeys.NodeId);
            newSession.VisitedNodes.Add(dayKeys.NodeId);

            lock (_sessionLock)
            {
                _activeSessionsByChannel[channelId] = newSession;
            }

            _logger.LogInformation("[Session Manager] Consensus session {SessionId} acquired for channel '{ChannelId}' date {Date} by {Initiator}. BasePath: {BasePath}.",
                newSession.SessionId, channelId, todayKey, initiatorNodeId, stagingOp.BasePath);

            return newSession;
        }
        finally
        {
            lock (_sessionLock)
            {
                _reservedChannels.Remove(channelId);
            }
        }
    }

    public ConsensusMergeSession? GetActiveSession(string channelId = PeerTrustConsensusChannel.CHANNEL_ID)
    {
        lock (_sessionLock)
        {
            return _activeSessionsByChannel.TryGetValue(channelId, out var session) ? session : null;
        }
    }

    public IReadOnlyCollection<string> GetActiveChannelIds()
    {
        lock (_sessionLock)
        {
            return _activeSessionsByChannel.Keys.ToList();
        }
    }

    public bool IsBusy(string channelId = PeerTrustConsensusChannel.CHANNEL_ID)
    {
        lock (_sessionLock)
        {
            return _activeSessionsByChannel.ContainsKey(channelId) || _reservedChannels.Contains(channelId);
        }
    }

    public void SetActiveTargetNode(string? targetNodeId, string channelId = PeerTrustConsensusChannel.CHANNEL_ID)
    {
        lock (_sessionLock)
        {
            if (_activeSessionsByChannel.TryGetValue(channelId, out var session))
            {
                session.TargetNodeId = targetNodeId ?? string.Empty;
                session.LastActivityUtc = DateTime.UtcNow;
            }
        }
    }

    public void QueueNodesToVisit(IEnumerable<string> nodeIds, string channelId = PeerTrustConsensusChannel.CHANNEL_ID)
    {
        lock (_sessionLock)
        {
            if (!_activeSessionsByChannel.TryGetValue(channelId, out var session) || nodeIds == null) return;

            foreach (var id in nodeIds)
            {
                if (string.IsNullOrWhiteSpace(id)) continue;
                if (string.Equals(id, _identity.NodeId, StringComparison.OrdinalIgnoreCase)) continue;

                // Knowing a node's records (sync sets) is not the same as having merged with it,
                // so only nodes already visited are skipped - otherwise nodes learned in a merge never get visited.
                if (!session.VisitedNodes.Contains(id) &&
                    !session.GaveUpNodes.Contains(id) &&
                    !session.NodesToVisit.Contains(id, StringComparer.OrdinalIgnoreCase))
                {
                    session.NodesToVisit.Enqueue(id);
                }
            }

            session.LastActivityUtc = DateTime.UtcNow;
        }
    }

    public bool TryPopNextNodeToVisit(out string? nodeId, string channelId = PeerTrustConsensusChannel.CHANNEL_ID)
    {
        lock (_sessionLock)
        {
            if (!_activeSessionsByChannel.TryGetValue(channelId, out var session))
            {
                nodeId = null;
                return false;
            }

            while (session.NodesToVisit.TryDequeue(out var candidate))
            {
                if (string.IsNullOrWhiteSpace(candidate)) continue;

                if (!session.VisitedNodes.Contains(candidate) &&
                    !session.GaveUpNodes.Contains(candidate) &&
                    !string.Equals(candidate, _identity.NodeId, StringComparison.OrdinalIgnoreCase))
                {
                    // Not marked visited here: only a finished merge or a valid delta from the node does that.
                    session.LastActivityUtc = DateTime.UtcNow;
                    nodeId = candidate;
                    return true;
                }
            }

            nodeId = null;
            return false;
        }
    }

    public void MarkNodeAsVisited(string nodeId, string channelId = PeerTrustConsensusChannel.CHANNEL_ID)
    {
        lock (_sessionLock)
        {
            if (!_activeSessionsByChannel.TryGetValue(channelId, out var session) || string.IsNullOrWhiteSpace(nodeId)) return;

            session.VisitedNodes.Add(nodeId);
            session.LastActivityUtc = DateTime.UtcNow;
        }
    }

    public bool RequeueNodeForRetry(string nodeId, int maxRetries, string channelId = PeerTrustConsensusChannel.CHANNEL_ID)
    {
        lock (_sessionLock)
        {
            if (!_activeSessionsByChannel.TryGetValue(channelId, out var session) || string.IsNullOrWhiteSpace(nodeId)) return false;

            if (session.VisitedNodes.Contains(nodeId)) return false;   // got its updates meanwhile

            session.VisitRetries.TryGetValue(nodeId, out var tries);
            if (tries >= maxRetries)
            {
                session.GaveUpNodes.Add(nodeId);
                return false;
            }

            session.VisitRetries[nodeId] = tries + 1;
            if (!session.NodesToVisit.Contains(nodeId, StringComparer.OrdinalIgnoreCase))
            {
                session.NodesToVisit.Enqueue(nodeId);
            }
            session.LastActivityUtc = DateTime.UtcNow;
            return true;
        }
    }

    public void GiveUpNode(string nodeId, string channelId = PeerTrustConsensusChannel.CHANNEL_ID)
    {
        lock (_sessionLock)
        {
            if (!_activeSessionsByChannel.TryGetValue(channelId, out var session) || string.IsNullOrWhiteSpace(nodeId)) return;
            session.GaveUpNodes.Add(nodeId);
        }
    }

    public bool IsNodeVisited(string nodeId, string channelId = PeerTrustConsensusChannel.CHANNEL_ID)
    {
        lock (_sessionLock)
        {
            return _activeSessionsByChannel.TryGetValue(channelId, out var session) && session.VisitedNodes.Contains(nodeId);
        }
    }

    public bool HasNodesToVisit(string channelId = PeerTrustConsensusChannel.CHANNEL_ID)
    {
        lock (_sessionLock)
        {
            return _activeSessionsByChannel.TryGetValue(channelId, out var session) && !session.NodesToVisit.IsEmpty;
        }
    }

    public void RegisterNodesInCurrentSyncSet(IEnumerable<string> nodeIds, string channelId = PeerTrustConsensusChannel.CHANNEL_ID)
    {
        lock (_sessionLock)
        {
            if (!_activeSessionsByChannel.TryGetValue(channelId, out var session) || nodeIds == null) return;

            foreach (var id in nodeIds)
            {
                if (!string.IsNullOrWhiteSpace(id))
                {
                    session.CurrentSyncSet.Add(id);
                }
            }

            session.LastActivityUtc = DateTime.UtcNow;
        }
    }

    public void AddSyncSetSignature(string signerNodeId, string targetNodeId, byte[] signature, int order = 0, string channelId = PeerTrustConsensusChannel.CHANNEL_ID)
    {
        lock (_sessionLock)
        {
            if (!_activeSessionsByChannel.TryGetValue(channelId, out var session) || string.IsNullOrWhiteSpace(signerNodeId)) return;

            var item = new ChainSignature
            {
                Order = order != 0 ? order : session.SyncSetSignatures.Count,
                SignerNodeId = signerNodeId,
                TargetNodeId = targetNodeId,
                Signature = signature ?? Array.Empty<byte>(),
                TimestampUtc = DateTime.UtcNow
            };

            session.SyncSetSignatures[ChainLinkKey(item)] = item;
            session.LastActivityUtc = DateTime.UtcNow;
        }
    }

    public void AddSyncSetSignature(ChainSignature link, string channelId = PeerTrustConsensusChannel.CHANNEL_ID)
    {
        lock (_sessionLock)
        {
            if (link == null || string.IsNullOrWhiteSpace(link.SignerNodeId) || link.Signature == null || link.Signature.Length == 0) return;
            if (!_activeSessionsByChannel.TryGetValue(channelId, out var session)) return;

            // Keyed per link, not per signer: one node signs several links in a session (one per merge),
            // and overwriting the earlier ones broke every chain that pointed at them.
            session.SyncSetSignatures[ChainLinkKey(link)] = new ChainSignature
            {
                Order = link.Order,
                SignerNodeId = link.SignerNodeId,
                TargetNodeId = link.TargetNodeId,
                TimestampUtc = link.TimestampUtc,
                Signature = link.Signature
            };
            session.LastActivityUtc = DateTime.UtcNow;
        }
    }

    private static string ChainLinkKey(ChainSignature link) =>
        $"{link.Order}|{link.SignerNodeId}|{link.TargetNodeId}";

    public List<ChainSignature> GetSyncSetSignatures(string channelId = PeerTrustConsensusChannel.CHANNEL_ID)
    {
        lock (_sessionLock)
        {
            if (!_activeSessionsByChannel.TryGetValue(channelId, out var session)) return new List<ChainSignature>();
            return session.SyncSetSignatures.Values.OrderBy(x => x.Order).ToList();
        }
    }

    public List<string> GetPastSyncSetNodes(string channelId = PeerTrustConsensusChannel.CHANNEL_ID)
    {
        lock (_sessionLock)
        {
            if (!_activeSessionsByChannel.TryGetValue(channelId, out var session)) return new List<string>();
            return session.PastSyncSet.ToList();
        }
    }


    public async Task<ConsensusSessionDeltaItem?> CreateLocalDeltaAsync(List<ChainSignature>? signatureChain = null, string channelId = PeerTrustConsensusChannel.CHANNEL_ID)
    {
        ConsensusMergeSession? session;
        lock (_sessionLock)
        {
            _activeSessionsByChannel.TryGetValue(channelId, out session);
        }

        if (session == null || session.StagingOperation == null) return null;

        string iterDir = session.StagingOperation.IterDir;
        string mergeDir = session.StagingOperation.MergeDir;

        // Same gate as the merge engine: a swap may be staging into IterDir right now.
        using var gate = await session.StagingOperation.LockAsync();

        if (!Directory.Exists(iterDir)) return null;

        var diffNodes = new List<PeerNodeHistoric>();
        var jsonOptions = new JsonSerializerOptions { WriteIndented = true };

        foreach (var iterFile in Directory.GetFiles(iterDir, "*.json"))
        {
            try
            {
                string nodeId = Path.GetFileNameWithoutExtension(iterFile);
                string iterJson = await File.ReadAllTextAsync(iterFile);

                PeerNodeHistoric? iterNode = null;
                iterNode = ConsensusNodeJson.ReadHistoric(iterJson);

                if (iterNode?.Identity == null || string.IsNullOrEmpty(iterNode.Identity.NodeId))
                    continue;

                string mergeFile = Path.Combine(mergeDir, $"{nodeId}.json");
                bool isDiff = false;

                if (!File.Exists(mergeFile))
                {
                    isDiff = true;
                }
                else
                {
                    string mergeJson = await File.ReadAllTextAsync(mergeFile);
                    PeerNodeHistoric? mergeNode = null;
                    mergeNode = ConsensusNodeJson.ReadHistoric(mergeJson);

                    if (mergeNode?.Identity == null ||
                        iterNode.Identity.SignatureTime > mergeNode.Identity.SignatureTime ||
                        iterNode.LastSeen > mergeNode.LastSeen)
                    {
                        isDiff = true;
                    }
                }

                if (isDiff)
                {
                    diffNodes.Add(iterNode);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Session Manager] Error comparing file {File} during delta diff generation.", iterFile);
            }
        }

        if (diffNodes.Count == 0)
        {
            _logger.LogInformation("[Session Manager] No differences detected between IterDir and MergeDir. Skipping local delta creation.");
            return null;
        }

        string deltaId = GenerateUniqueDeltaId(session.GeneratedDeltasDir);
        string deltaFolderPath = Path.Combine(session.GeneratedDeltasDir, deltaId);
        string nodesFolderPath = Path.Combine(deltaFolderPath, "nodes");

        Directory.CreateDirectory(nodesFolderPath);

        foreach (var node in diffNodes)
        {
            string nodeFilePath = Path.Combine(nodesFolderPath, $"{node.Identity.NodeId}.json");
            string nodeJson = JsonSerializer.Serialize(node, jsonOptions);
            await File.WriteAllTextAsync(nodeFilePath, nodeJson);
        }

        var chain = signatureChain ?? session.SyncSetSignatures.Values.OrderBy(x => x.Order).ToList();
        DateTime createdUtc = DateTime.UtcNow;
        byte[] deltaSignature = ComputeDeltaSignature(_identity.NodeId, createdUtc, chain, session.DayKeySignatureKey);

        var deltaItem = new ConsensusSessionDeltaItem
        {
            DeltaId = deltaId,
            SourceNodeId = _identity.NodeId,
            CreatedUtc = createdUtc,
            DeltaFolderPath = deltaFolderPath,
            SignatureChain = chain,
            Signature = deltaSignature
        };

        string metadataFilePath = Path.Combine(deltaFolderPath, "delta.json");
        string metadataJson = JsonSerializer.Serialize(deltaItem, jsonOptions);
        await File.WriteAllTextAsync(metadataFilePath, metadataJson);

        session.LocalDeltas[deltaId] = deltaItem;
        session.LastActivityUtc = DateTime.UtcNow;

        _logger.LogInformation("[Session Manager] Created local delta folder {DeltaId} in generated_deltas containing {Count} diff node files.",
            deltaId, diffNodes.Count);

        return deltaItem;
    }

    public bool PrepareDownloadedDeltaFolder(out string deltaId, out string destFolderPath, string channelId = PeerTrustConsensusChannel.CHANNEL_ID)
    {
        ConsensusMergeSession? session;
        lock (_sessionLock)
        {
            _activeSessionsByChannel.TryGetValue(channelId, out session);
        }

        if (session == null)
        {
            deltaId = string.Empty;
            destFolderPath = string.Empty;
            return false;
        }

        deltaId = GenerateUniqueDeltaId(session.DownloadedDeltasDir);
        destFolderPath = Path.Combine(session.DownloadedDeltasDir, deltaId);

        try
        {
            Directory.CreateDirectory(Path.Combine(destFolderPath, "nodes"));
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Session Manager] Failed preparing downloaded delta directory at {Path}.", destFolderPath);
            deltaId = string.Empty;
            destFolderPath = string.Empty;
            return false;
        }
    }

    public async Task<DeltaRegistration> RegisterDownloadedDeltaAsync(string sourceNodeId, string downloadedFolderPath, List<ChainSignature>? signatureChain = null, string channelId = PeerTrustConsensusChannel.CHANNEL_ID)
    {
        ConsensusMergeSession? session;
        lock (_sessionLock)
        {
            _activeSessionsByChannel.TryGetValue(channelId, out session);
        }

        if (session == null || !Directory.Exists(downloadedFolderPath)) return DeltaRegistration.Failed;

        string deltaId = Path.GetFileName(downloadedFolderPath);
        var nodeVerifyKeys = LoadNodeVerifyKeysFromDeltaFolder(downloadedFolderPath);

        ConsensusSessionDeltaItem? incomingMetadata = null;
        string sourceMetadataFile = Path.Combine(downloadedFolderPath, "delta.json");
        if (File.Exists(sourceMetadataFile))
        {
            try
            {
                string metaJson = File.ReadAllText(sourceMetadataFile);
                incomingMetadata = JsonSerializer.Deserialize<ConsensusSessionDeltaItem>(metaJson);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Session Manager] Failed reading delta.json metadata from {FolderPath}.", downloadedFolderPath);
            }
        }

        var chain = signatureChain ?? incomingMetadata?.SignatureChain ?? new List<ChainSignature>();

        var (chainCheck, unconfirmedSigners, failedLink) = await VerifySignatureChainAsync(chain, nodeVerifyKeys, session);
        if (chainCheck == DeltaRegistration.Registered && unconfirmedSigners.Count > 0)
        {
            // Nodes we have not met yet are part of the new consensus: take the delta in. Its node records still
            // need valid self-signatures when staged, and they stay doubtful until another source reports them too.
            _logger.LogInformation("[Session Manager] Delta {DeltaId} from {SourceNodeId}: signer(s) {Signers} not known here yet - taken in on trust until another source confirms them.",
                deltaId, sourceNodeId, string.Join(", ", unconfirmedSigners));
            foreach (var s in unconfirmedSigners) session.UnconfirmedSigners.TryAdd(s, 0);
        }
        if (chainCheck != DeltaRegistration.Registered)
        {
            _logger.LogWarning("[Session Manager] Early verification failed for downloaded delta {DeltaId} from {SourceNodeId}. Purging folder.", deltaId, sourceNodeId);
            if (chainCheck == DeltaRegistration.InvalidSignature && session.StagingOperation != null)
            {
                // A link that fails against this day's key for its signer: a real discrepancy, kept for gossip
                ConsensusEvidenceStore.Write(session.StagingOperation.EvidenceDir, new ConsensusEvidence
                {
                    Kind = "bad-chain",
                    SubjectNodeId = sourceNodeId,
                    ConsensusDate = session.StagingOperation.TargetDate,
                    DetectedByNodeId = _identity.NodeId,
                    Reason = failedLink == null
                        ? "Malformed signature chain."
                        : $"Chain link {failedLink.Order} {failedLink.SignerNodeId}->{failedLink.TargetNodeId} does not verify against the signer's key for this day.",
                    Chain = chain,
                    PenalizedNodeIds = new List<string> { sourceNodeId },
                }, _logger);
            }
            try
            {
                Directory.Delete(downloadedFolderPath, recursive: true);
            }
            catch { }
            return chainCheck;
        }

        var deltaItem = new ConsensusSessionDeltaItem
        {
            DeltaId = deltaId,
            SourceNodeId = sourceNodeId,
            CreatedUtc = incomingMetadata?.CreatedUtc ?? DateTime.UtcNow,
            DeltaFolderPath = downloadedFolderPath,
            SignatureChain = chain,
            Signature = incomingMetadata?.Signature ?? Array.Empty<byte>(),
            UnconfirmedSigners = unconfirmedSigners,
        };

        File.WriteAllText(sourceMetadataFile, JsonSerializer.Serialize(deltaItem, new JsonSerializerOptions { WriteIndented = true }));

        session.PendingDeltasToMerge[deltaId] = deltaItem;
        session.LastActivityUtc = DateTime.UtcNow;

        _logger.LogInformation("[Session Manager] Early verification passed. Registered downloaded delta {DeltaId} from {SourceNodeId}.", deltaId, sourceNodeId);
        return DeltaRegistration.Registered;
    }

    public List<ConsensusSessionDeltaItem> GetPendingDeltasToMerge(string channelId = PeerTrustConsensusChannel.CHANNEL_ID)
    {
        lock (_sessionLock)
        {
            if (!_activeSessionsByChannel.TryGetValue(channelId, out var session)) return new List<ConsensusSessionDeltaItem>();
            return session.PendingDeltasToMerge.Values.ToList();
        }
    }

    public List<string> GetUnconfirmedSigners(string deltaId, string channelId = PeerTrustConsensusChannel.CHANNEL_ID)
    {
        lock (_sessionLock)
        {
            if (!_activeSessionsByChannel.TryGetValue(channelId, out var session) || string.IsNullOrEmpty(deltaId)) return new List<string>();
            return session.PendingDeltasToMerge.TryGetValue(deltaId, out var item) ? item.UnconfirmedSigners.ToList() : new List<string>();
        }
    }

    public bool HasPendingDeltas(string channelId = PeerTrustConsensusChannel.CHANNEL_ID)
    {
        lock (_sessionLock)
        {
            if (!_activeSessionsByChannel.TryGetValue(channelId, out var session)) return false;
            return !session.PendingDeltasToMerge.IsEmpty;
        }
    }

    public bool RemovePendingDelta(string deltaId, bool deleteFolder = false, string channelId = PeerTrustConsensusChannel.CHANNEL_ID)
    {
        lock (_sessionLock)
        {
            if (!_activeSessionsByChannel.TryGetValue(channelId, out var session) || string.IsNullOrEmpty(deltaId)) return false;

            bool removed = session.PendingDeltasToMerge.TryRemove(deltaId, out var deltaItem);
            if (removed && deleteFolder && deltaItem != null && !string.IsNullOrEmpty(deltaItem.DeltaFolderPath) && Directory.Exists(deltaItem.DeltaFolderPath))
            {
                try
                {
                    Directory.Delete(deltaItem.DeltaFolderPath, recursive: true);
                    _logger.LogInformation("[Session Manager] Purged pending delta folder {DeltaId} from disk.", deltaId);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[Session Manager] Failed to delete delta folder {FolderPath}.", deltaItem.DeltaFolderPath);
                }
            }

            return removed;
        }
    }

    public ConsensusSessionDeltaItem? GetLocalDelta(string deltaId, string channelId = PeerTrustConsensusChannel.CHANNEL_ID)
    {
        lock (_sessionLock)
        {
            if (!_activeSessionsByChannel.TryGetValue(channelId, out var session) || string.IsNullOrEmpty(deltaId)) return null;
            session.LocalDeltas.TryGetValue(deltaId, out var deltaItem);
            return deltaItem;
        }
    }

    public List<string> GetDeltaFolderPathsBatch(List<string> deltaIds, string channelId = PeerTrustConsensusChannel.CHANNEL_ID)
    {
        var folderPaths = new List<string>();
        lock (_sessionLock)
        {
            if (!_activeSessionsByChannel.TryGetValue(channelId, out var session) || deltaIds == null || deltaIds.Count == 0) return folderPaths;

            foreach (var id in deltaIds)
            {
                if (session.LocalDeltas.TryGetValue(id, out var delta) && Directory.Exists(delta.DeltaFolderPath))
                {
                    folderPaths.Add(delta.DeltaFolderPath);
                }
            }
        }

        return folderPaths;
    }


    public async Task<PeerNodeHistoric?> GetNodeFromDeltaOrStagingHierarchyAsync(string deltaId, string nodeId, string channelId = PeerTrustConsensusChannel.CHANNEL_ID)
    {
        ConsensusMergeSession? session;
        lock (_sessionLock)
        {
            _activeSessionsByChannel.TryGetValue(channelId, out session);
        }

        if (session == null || string.IsNullOrEmpty(nodeId)) return null;

        if (!string.IsNullOrEmpty(deltaId))
        {
            ConsensusSessionDeltaItem? deltaItem = GetLocalDelta(deltaId, channelId);
            if (deltaItem == null && session.PendingDeltasToMerge.TryGetValue(deltaId, out var pending))
            {
                deltaItem = pending;
            }

            if (deltaItem != null && Directory.Exists(deltaItem.DeltaFolderPath))
            {
                string deltaNodeFile = Path.Combine(deltaItem.DeltaFolderPath, "nodes", $"{nodeId}.json");
                if (File.Exists(deltaNodeFile))
                {
                    try
                    {
                        string json = await File.ReadAllTextAsync(deltaNodeFile);
                        PeerNodeHistoric? peerFromDelta = null;
                        peerFromDelta = ConsensusNodeJson.ReadHistoric(json);

                        if (peerFromDelta != null) return peerFromDelta;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[Session Manager] Error reading node {NodeId} from delta folder {DeltaId}.", nodeId, deltaId);
                    }
                }
            }
        }

        if (session.StagingOperation != null)
        {
            return await _mergeEngine.GetNodeFromStagingHierarchyAsync(session.StagingOperation, nodeId);
        }

        return null;
    }

    public async Task FinalizeIterationStepAsync(string channelId = PeerTrustConsensusChannel.CHANNEL_ID)
    {
        ConsensusMergeSession? session;
        lock (_sessionLock)
        {
            _activeSessionsByChannel.TryGetValue(channelId, out session);
        }

        if (session == null || session.StagingOperation == null) return;

        _logger.LogInformation("[Session Manager] Finalizing iteration step for session {SessionId}.", session.SessionId);
        await _mergeEngine.FinalizeIterationAsync(session.StagingOperation);

        lock (_sessionLock)
        {
            foreach (var nodeId in session.CurrentSyncSet)
            {
                session.PastSyncSet.Add(nodeId);
            }
            session.CurrentSyncSet.Clear();
            session.LastActivityUtc = DateTime.UtcNow;
        }
    }

    public async Task FinalizeSessionAsync(string channelId = PeerTrustConsensusChannel.CHANNEL_ID)
    {
        ConsensusMergeSession? sessionToFinalize;

        lock (_sessionLock)
        {
            _activeSessionsByChannel.TryGetValue(channelId, out sessionToFinalize);
            _activeSessionsByChannel.Remove(channelId);
        }

        if (sessionToFinalize?.StagingOperation != null)
        {
            _logger.LogInformation("[Session Manager] Finalizing total consensus merge for session {SessionId}.", sessionToFinalize.SessionId);
            try
            {
                await _mergeEngine.FinalizeTotalMergeAsync(sessionToFinalize.StagingOperation);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Session Manager] Failed to finalize session {SessionId}.", sessionToFinalize.SessionId);
            }
        }
    }

    public async Task AbortSessionAsync(string channelId = PeerTrustConsensusChannel.CHANNEL_ID)
    {
        ConsensusMergeSession? sessionToAbort;

        lock (_sessionLock)
        {
            _activeSessionsByChannel.TryGetValue(channelId, out sessionToAbort);
            _activeSessionsByChannel.Remove(channelId);
        }

        if (sessionToAbort?.StagingOperation != null)
        {
            _logger.LogWarning("[Session Manager] Aborting consensus session {SessionId}. Purging workspace.", sessionToAbort.SessionId);
            try
            {
                await _mergeEngine.AbortMergeOperationAsync(sessionToAbort.StagingOperation);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Session Manager] Error during session abort cleanup for {SessionId}.", sessionToAbort.SessionId);
            }
        }
    }

    private string GenerateUniqueDeltaId(string parentDir)
    {
        string candidate;
        string fullPath;
        do
        {
            candidate = Guid.NewGuid().ToString("N");
            fullPath = Path.Combine(parentDir, candidate);
        } while (Directory.Exists(fullPath));

        return candidate;
    }

    private byte[] ComputeDeltaSignature(string sourceNodeId, DateTime createdUtc, List<ChainSignature> chain, byte[] daySignatureKey)
    {
        if (daySignatureKey == null || daySignatureKey.Length == 0) return Array.Empty<byte>();

        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        writer.Write(sourceNodeId ?? string.Empty);
        writer.Write(createdUtc.Ticks);

        var sortedChain = (chain ?? new List<ChainSignature>()).OrderBy(x => x.Order).ToList();
        var lastSig = sortedChain.LastOrDefault();

        writer.Write(sortedChain.Count);
        if (lastSig != null && lastSig.Signature != null)
        {
            writer.Write(lastSig.Order);
            writer.Write(lastSig.SignerNodeId ?? string.Empty);
            writer.Write(lastSig.Signature);
        }

        writer.Flush();
        byte[] payloadHash = SHA256.HashData(ms.ToArray());
        return P2PCrypto.SignData(payloadHash, daySignatureKey);
    }

    private Dictionary<string, byte[]> LoadNodeVerifyKeysFromDeltaFolder(string deltaFolderPath)
    {
        var result = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        string nodesDir = Path.Combine(deltaFolderPath, "nodes");

        if (!Directory.Exists(nodesDir)) return result;

        foreach (var file in Directory.GetFiles(nodesDir, "*.json"))
        {
            try
            {
                string json = File.ReadAllText(file);
                PeerNodeHistoric? peer = null;
                peer = ConsensusNodeJson.ReadHistoric(json);

                if (peer?.Identity != null && !string.IsNullOrEmpty(peer.Identity.NodeId) && peer.Identity.PublicVerifyKey?.Length > 0)
                {
                    result[peer.Identity.NodeId] = peer.Identity.PublicVerifyKey;
                }
            }
            catch { }
        }

        return result;
    }

    /// <summary>Async so the historic-key fallback lookup is awaited instead of blocked on (was .GetAwaiter().GetResult()).</summary>
    /// <summary>
    /// Registered = every link checks out; InvalidSignature = a link fails against a KNOWN key (tampering);
    /// UnverifiableSigner = a signer's key is not known here (e.g. a node only the sender has met): not proof of
    /// tampering, so the caller skips the delta without a reputation penalty.
    /// </summary>
    private static byte[] ChainLinkHash(byte[]? previousSignature, ChainSignature sig)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        if (previousSignature != null && previousSignature.Length > 0)
        {
            writer.Write(previousSignature.Length);
            writer.Write(previousSignature);
        }

        writer.Write(sig.Order);
        writer.Write(sig.SignerNodeId);
        writer.Write(sig.TargetNodeId ?? string.Empty);
        writer.Write(sig.TimestampUtc.Ticks);
        writer.Flush();

        return SHA256.HashData(ms.ToArray());
    }

    /// <summary>
    /// Registered = every link checks out, except links by signers we have no key for at all (returned as
    /// unconfirmed: new nodes, taken in on trust). InvalidSignature = a link fails against the signer's key for
    /// this consensus day (from the delta itself or our staging) - a real discrepancy. A link that only fails
    /// against an older key from the peer tracker is not proof (keys rotate daily), so it counts as unconfirmed.
    /// </summary>
    private async Task<(DeltaRegistration Result, List<string> Unconfirmed, ChainSignature? FailedLink)> VerifySignatureChainAsync(List<ChainSignature> chain, Dictionary<string, byte[]> nodeVerifyKeys, ConsensusMergeSession? session)
    {
        var unconfirmed = new List<string>();
        if (chain == null || chain.Count == 0) return (DeltaRegistration.Registered, unconfirmed, null);

        // The links of one session form a tree, not one line: every merge adds a link of order n+1 that
        // points at SOME link of order n, and several merges in parallel produce several links of the same
        // order. So each link must verify against at least one link of the previous order (or none at the start).
        var sortedChain = chain.Where(x => x != null).OrderBy(x => x.Order).ToList();
        if (sortedChain.Count != chain.Count) return (DeltaRegistration.InvalidSignature, unconfirmed, null);
        int minOrder = sortedChain[0].Order;

        foreach (var sig in sortedChain)
        {
            if (sig == null || string.IsNullOrEmpty(sig.SignerNodeId)) return (DeltaRegistration.InvalidSignature, unconfirmed, sig);

            // Every key we know for the signer: this day's (delta, staging) and the tracker's (possibly an older day's)
            var dayKeys = new List<byte[]>();
            if (nodeVerifyKeys.TryGetValue(sig.SignerNodeId, out var deltaKey) && deltaKey?.Length > 0) dayKeys.Add(deltaKey);
            if (session?.StagingOperation != null)
            {
                var historicPeer = await _mergeEngine.GetNodeFromStagingHierarchyAsync(session.StagingOperation, sig.SignerNodeId);
                var stagedKey = historicPeer?.Identity?.PublicVerifyKey;
                if (stagedKey?.Length > 0 && !dayKeys.Any(k => k.AsSpan().SequenceEqual(stagedKey))) dayKeys.Add(stagedKey);
            }
            var trackerKey = _peerTracker.GetPeer(sig.SignerNodeId)?.Identity?.PublicVerifyKey;
            var keys = new List<byte[]>(dayKeys);
            if (trackerKey?.Length > 0 && !keys.Any(k => k.AsSpan().SequenceEqual(trackerKey))) keys.Add(trackerKey);

            if (keys.Count == 0)
            {
                // A node we have not met: nothing to check its link against yet
                if (!unconfirmed.Contains(sig.SignerNodeId, StringComparer.OrdinalIgnoreCase)) unconfirmed.Add(sig.SignerNodeId);
                continue;
            }

            var candidates = sortedChain
                .Where(x => x.Order == sig.Order - 1 && x.Signature != null && x.Signature.Length > 0)
                .Select(x => (byte[]?)x.Signature)
                .ToList();
            // First link of the session, or the predecessor isn't in this chain (it came from a part of the tree we didn't get)
            if (candidates.Count == 0 || sig.Order == minOrder) candidates.Add(null);

            bool ok = false;
            foreach (var key in keys)
            {
                foreach (var previousSignature in candidates)
                {
                    if (P2PCrypto.TestSignature(ChainLinkHash(previousSignature, sig), key, sig.Signature))
                    {
                        ok = true;
                        break;
                    }
                }
                if (ok) break;
            }

            if (!ok)
            {
                if (dayKeys.Count == 0)
                {
                    // Only an older key to compare with: the signer may simply have rotated keys since
                    _logger.LogInformation("[Session Manager] Chain link {Order} {Signer}->{Target}: only an older key of {Signer} is known here; not counted against anyone.",
                        sig.Order, sig.SignerNodeId, sig.TargetNodeId, sig.SignerNodeId);
                    if (!unconfirmed.Contains(sig.SignerNodeId, StringComparer.OrdinalIgnoreCase)) unconfirmed.Add(sig.SignerNodeId);
                    continue;
                }

                _logger.LogWarning("[Session Manager] Chain link {Order} {Signer}->{Target} does not verify against any link of order {Prev}.",
                    sig.Order, sig.SignerNodeId, sig.TargetNodeId, sig.Order - 1);
                return (DeltaRegistration.InvalidSignature, unconfirmed, sig);
            }
        }

        return (DeltaRegistration.Registered, unconfirmed, null);
    }
}
