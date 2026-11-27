// [full-implementation]
using System.Collections.Concurrent;
using System.Data;
using System.Net.Http.Json;
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
using P2PModule.Services.Rating;

namespace P2PModule.Services.Maintenance;



/// <summary>
/// Data transfer object carried in progressive streams for pairwise node record swaps.
/// </summary>
public class ConsensusNodeSwapPayload
{
    public List<PeerNodeTransferred> Nodes { get; set; } = new();
    /// <summary>0-based number of this chunk and how many chunks the swap has (0 = sender too old to say).</summary>
    public int ChunkIndex { get; set; }
    public int ChunkCount { get; set; }
}

/// <summary>
/// Consensus request wrapper carrying identity signature proofs and non-transitive flags.
/// </summary>
public class ConsensusRequest
{
    public string NodeIdFrom { get; set; } = string.Empty;
    public DateTime TimeStamp { get; set; } = DateTime.UtcNow;
    public bool NonTransitive { get; set; } = true;
    public string Nonce { get; set; } = string.Empty;
    public byte[] Signature { get; set; } = Array.Empty<byte>();
    public double? StartingRating { get; set; }
    /// <summary>
    /// Id of the consensus channel (see Common.Interfaces.Consensus.IConsensusChannel) this
    /// negotiation is for. Defaults to the built-in peer-trust channel so today's single-channel
    /// behavior is completely unchanged - this field is what lets a future second channel's
    /// handler run its own independent start-ping/merge-init handshake over the same command
    /// channel without colliding with this one.
    /// </summary>
    public string ChannelId { get; set; } = PeerTrustConsensusChannel.CHANNEL_ID;

    /// <summary>The node this request is meant for (signed): a copy replayed to another node is refused there.</summary>
    public string NodeIdTo { get; set; } = string.Empty;

    /// <summary>The sender's current self-signed identity: tells the receiver which key the request is signed with.</summary>
    public PeerImmutableData? SenderIdentity { get; set; }

    /// <summary>
    /// The sender's earlier day keys signing the hash of SenderIdentity: proof that the node itself moved to the
    /// new key. Without it a key the receiver has not seen for this node is a discrepancy, not a key change.
    /// </summary>
    public List<SignatureEnvelope> KeyLineage { get; set; } = new();

    /// <summary>
    /// Merge init only: the nodes the sender holds in its merge so far. The target answers ALREADY_SYNCED only when
    /// the sender lacks none of the target's nodes - having the sender's updates is not enough, the sender may
    /// still be missing the target's (a node then never got a record nobody else forwarded to it).
    /// </summary>
    public List<string> HeldNodeIds { get; set; } = new();

    public byte[] GetHash()
    {
        string identityHash = SenderIdentity != null ? Convert.ToBase64String(SenderIdentity.GetNodeDataHash()) : string.Empty;
        string held = string.Join(",", HeldNodeIds.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
        return SHA256.HashData(
            Encoding.UTF8.GetBytes($"{NodeIdFrom}|{NodeIdTo}|{TimeStamp:o}|{NonTransitive}|{Nonce}|{ChannelId}|{identityHash}|{held}"));
    }
}

/// <summary>Outcome of checking a signed consensus request (start ping, merge init).</summary>
public enum SignedRequestVerdict
{
    Ok,
    /// <summary>Signed with a key we had not seen, and the node's earlier key vouches for it (or we knew no key for it at all).</summary>
    OkNewKey,
    Unsigned,
    /// <summary>Signed as another node than the one on this link.</summary>
    WrongSender,
    /// <summary>Signed for another node than us (replayed).</summary>
    WrongTarget,
    /// <summary>Too old or seen before.</summary>
    Replay,
    /// <summary>Signed with a key that differs from what we know for this node, with nothing showing the node changed keys.</summary>
    KeyChangeUnproven,
    /// <summary>The signature matches no key at all, not even the one the request itself names.</summary>
    BadSignature,
}

/// <summary>
/// Request payload for acquiring cryptographic signatures over exchanged sync node sets.
/// </summary>
public class ConsensusSignatureRequestPayload
{
    public string RequesterNodeId { get; set; } = string.Empty;
    public string TargetNodeId { get; set; } = string.Empty;
    public List<string> ExchangedNodeIds { get; set; } = new();
    public int SignatureOrder { get; set; }
    public ChainSignature? PreviousSignature { get; set; }
    public List<ChainSignature> FullSignatureChain { get; set; } = new();
}

/// <summary>
/// Notification ping carrying delta availability and source/sending signature chains.
/// </summary>
/// <summary>Echo round: which record (id -> short version fingerprint) a node holds, and a digest over all of them.</summary>
public class ConsensusEcho
{
    public string Digest { get; set; } = string.Empty;
    public Dictionary<string, string> Records { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public class ConsensusNodePullRequest
{
    public List<string> NodeIds { get; set; } = new();
}

public class ConsensusDeltaPing
{
    public string DeltaFromNode { get; set; } = string.Empty;
    public List<ChainSignature> SourceSignatureChain { get; set; } = new();
    public string DeltaIdentifier { get; set; } = string.Empty;
    public List<ChainSignature> SignatureChain { get; set; } = new();
    public byte[] DeltaVerifyKey { get; set; } = Array.Empty<byte>();
    public HashSet<string> Nodes { get; set; } = new();
}

/// <summary>
/// Payload sent to request a specific delta by identifier.
/// </summary>
public class ConsensusDeltaRequest
{
    public string DeltaIdentifier { get; set; } = string.Empty;
}

/// <summary>
/// Disk envelope metadata for tracking downloaded/ingested deltas.
/// </summary>
public class ConsensusDeltaOnDisk
{
    public string DeltaFromNode { get; set; } = string.Empty;
    public string DeltaIdentifier { get; set; } = string.Empty;
    public List<string> NodeIds { get; set; } = new();
}

/// <summary>
/// Strongly-typed document payload for transferring sharded delta node data over progressive channels.
/// </summary>
public class ConsensusDeltaDocument
{
    public List<PeerNodeHistoric> DeltaNodes { get; set; } = new();
}

public interface IConsensusProtocolHandler
{
    /// <summary>
    /// Attempts to initiate a local daily consensus session on demand.
    /// </summary>
    Task<bool> TriggerConsensusAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// True when at least one peer is connected and handshaked. Consensus is not started without one
    /// (there is nobody to merge with, the session would only idle and fill the log).
    /// </summary>
    bool HasConnectedPeers();
}


/// <summary>
/// Drives active epidemic consensus workflow and responds to incoming network pings.
/// Handles two-stage handshakes, progressive node/delta streaming, and anti-tamper signature checks.
/// </summary>
public class ConsensusProtocolHandler : IConsensusProtocolHandler, IHostedService, IDisposable
{
    private readonly IConsensusSessionManager _sessionManager;
    private readonly IConsensusMergeEngine _mergeEngine;
    private readonly IConsensusStore _consensusStore;
    private readonly INodeIdentityService _identity;
    private readonly IPeerTracker _peerTracker;
    private readonly IPeerConnectionManager _connectionManager;
    private readonly IP2PRouterService _routerService;
    private readonly IRatingService _ratingService;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptionsMonitor<P2PSettings> _p2pSettings;
    private readonly IEvilBehaviour _evil;
    private readonly ILogger<ConsensusProtocolHandler> _logger;

    private CancellationTokenSource? _sessionLoopCts;
    private const int NodeSwapBatchSize = 50;
    private const int DeltaNodeBatchSize = 40;
    private const int RetryDelayMs = 1500;
    /// <summary>How many times a BUSY node is put back into the visit queue before it is given up for this session.</summary>
    private const int MaxBusyRetries = 20;
    private const int MaxPullIds = 64;
    /// <summary>How many times an unreachable node is retried before it is given up for this session.</summary>
    private const int MaxConnectRetries = 5;

    public ConsensusProtocolHandler(
        IConsensusSessionManager sessionManager,
        IConsensusMergeEngine mergeEngine,
        IConsensusStore consensusStore,
        INodeIdentityService identity,
        IPeerTracker peerTracker,
        IPeerConnectionManager connectionManager,
        IP2PRouterService routerService,
        IRatingService ratingService,
        IServiceScopeFactory scopeFactory,
        IOptionsMonitor<P2PSettings> p2pSettings,
        IEvilBehaviour evil,
        ILogger<ConsensusProtocolHandler> logger)
    {
        _evil = evil;
        _sessionManager = sessionManager;
        _mergeEngine = mergeEngine;
        _consensusStore = consensusStore;
        _identity = identity;
        _peerTracker = peerTracker;
        _connectionManager = connectionManager;
        _routerService = routerService;
        _ratingService = ratingService;
        _scopeFactory = scopeFactory;
        _p2pSettings = p2pSettings;
        _logger = logger;
    }


    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("[Consensus Protocol] Initializing Protocol Handler. Registering idle start ping listener.");
        _routerService.RegisterCommandHandler("ConsensusStartPing", HandleIncomingStartPingAsync);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("[Consensus Protocol] Stopping Protocol Handler.");
        _sessionLoopCts?.Cancel();
        UnregisterConsensusNetworkHandlers();
        _routerService.UnregisterCommandHandler("ConsensusStartPing");
        return Task.CompletedTask;
    }

    public bool HasConnectedPeers() =>
        _connectionManager.GetActiveConnections().Any(c =>
            c.IsHandshaked && !string.Equals(c.NodeId, _identity.NodeId, StringComparison.OrdinalIgnoreCase));

    public async Task<bool> TriggerConsensusAsync(CancellationToken cancellationToken = default)
    {
        if (!HasConnectedPeers())
        {
            _logger.LogDebug("[Consensus Protocol] Not starting consensus - no peers connected.");
            return false;
        }

        if (_sessionManager.IsBusy())
        {
            _logger.LogWarning("[Consensus Protocol] Cannot trigger manual consensus - session is already busy.");
            return false;
        }

        var session = await _sessionManager.TryAcquireSessionAsync(_identity.NodeId);
        if (session == null)
        {
            return false;
        }

        SeedVisitQueueFromPeerTracker();
        RegisterConsensusNetworkHandlers();
        LogConsensusEvent("start", new Dictionary<string, object?> { ["role"] = "initiator", ["known"] = KnownPeerCount(), ["bytes"] = TrafficCounter.ConsensusBytes, ["msgs"] = TrafficCounter.ConsensusMessages });

        _sessionLoopCts?.Cancel();
        _sessionLoopCts = new CancellationTokenSource();

        _ = Task.Run(() => ExecuteConsensusWorkflowLoopAsync(_sessionLoopCts.Token), cancellationToken);
        return true;
    }


    private async Task ExecuteConsensusWorkflowLoopAsync(CancellationToken ct)
    {
        _logger.LogInformation("[Consensus Protocol] Starting active consensus workflow loop.");

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var session = _sessionManager.GetActiveSession();
                if (session == null) break;

                if (IsMidnightBoundaryReached())
                {
                    _logger.LogWarning("[Consensus Protocol] Midnight UTC boundary reached. Finalizing consensus session.");
                    await CompleteAndFinalizeSessionAsync("midnight");
                    break;
                }

                while ((_sessionManager.TryPopNextNodeToVisit(out var candidateNodeId) || !string.IsNullOrEmpty(session.TargetNodeId)) && !ct.IsCancellationRequested)
                {
                    string? targetNodeId = !string.IsNullOrEmpty(session.TargetNodeId) ? session.TargetNodeId : candidateNodeId;
                    if (string.IsNullOrEmpty(targetNodeId)) continue;

                    bool connected = await EnsurePeerConnectionAsync(targetNodeId);
                    if (!connected)
                    {
                        // A node that is down is not retried in this session (no point; it would only delay the loop)
                        bool requeued = !_connectionManager.IsOffline(targetNodeId) &&
                                        _sessionManager.RequeueNodeForRetry(targetNodeId, MaxConnectRetries);
                        if (!requeued) _sessionManager.RequeueNodeForRetry(targetNodeId, 0);   // records it as given up
                        _logger.LogWarning("[Consensus Protocol] Connection to {TargetNodeId} failed. {Action}.", targetNodeId, requeued ? "Re-queued" : "Giving up for this session (not marked visited)");
                        _sessionManager.SetActiveTargetNode(null);
                        await Task.Delay(500, ct);
                        continue;
                    }

                    // Step 1: Pairwise Handshake (Ping + Init Request)
                    bool isLockedIn = string.Equals(session.TargetNodeId, targetNodeId, StringComparison.OrdinalIgnoreCase);

                    if (!isLockedIn)
                    {
                        string startStatus = await SendStartPingAsync(targetNodeId, ct);
                        if (!string.Equals(startStatus, "AVAILABLE", StringComparison.OrdinalIgnoreCase) &&
                            !string.Equals(startStatus, "STARTING_UP", StringComparison.OrdinalIgnoreCase))
                        {
                            bool offline = _connectionManager.IsOffline(targetNodeId);
                            bool requeued = !offline && _sessionManager.RequeueNodeForRetry(targetNodeId, MaxBusyRetries);
                            if (!requeued) _sessionManager.RequeueNodeForRetry(targetNodeId, 0);
                            _logger.LogInformation("[Consensus Protocol] Target {TargetNodeId} start ping returned '{Status}'. {Action}.", targetNodeId, startStatus,
                                offline ? "It is offline; skipping it this session" : requeued ? "Re-queued" : "Giving up for this session");
                            await Task.Delay(RetryDelayMs, ct);
                            continue;
                        }

                        string initStatus = await SendMergeInitRequestAsync(targetNodeId, ct);

                        if (string.Equals(initStatus, "OK_AVAILABLE", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(initStatus, "AVAILABLE", StringComparison.OrdinalIgnoreCase))
                        {
                            _sessionManager.SetActiveTargetNode(targetNodeId);
                            isLockedIn = true;
                        }
                        else if (initStatus.StartsWith("ALREADY_SYNCED", StringComparison.OrdinalIgnoreCase))
                        {
                            // The target already holds OUR updates. If it names nodes we lack, pull just those
                            // records from it instead of a full merge.
                            var lacking = initStatus.Length > "ALREADY_SYNCED|".Length
                                ? initStatus.Substring("ALREADY_SYNCED|".Length).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList()
                                : new List<string>();
                            _logger.LogInformation("[Consensus Protocol] Target {TargetNodeId} reports ALREADY_SYNCED{Lacking}.", targetNodeId,
                                lacking.Count > 0 ? $"; pulling the {lacking.Count} record(s) we lack from it" : ". Not chasing it further this session");
                            if (lacking.Count > 0 && await PullNodesAsync(targetNodeId, lacking, session, ct) > 0)
                            {
                                // What we pulled is new to our old partners too: pass it on as a delta, like a merge result
                                await CreateAndBroadcastLocalDeltaAsync(session, ct);
                                await FinalizeIterationAndReportAsync(session);
                            }
                            _sessionManager.GiveUpNode(targetNodeId);
                            continue;
                        }
                        else if (initStatus.StartsWith("BUSY", StringComparison.OrdinalIgnoreCase) ||
                                 initStatus.StartsWith("WTF_BUSY", StringComparison.OrdinalIgnoreCase))
                        {
                            bool requeued = _sessionManager.RequeueNodeForRetry(targetNodeId, MaxBusyRetries);
                            _logger.LogInformation("[Consensus Protocol] Target {TargetNodeId} is BUSY ({Status}). {Action}.", targetNodeId, initStatus, requeued ? "Re-queued with retry backoff" : "Giving up for this session");
                            await Task.Delay(RetryDelayMs, ct);
                            continue;
                        }
                        else
                        {
                            _logger.LogWarning("[Consensus Protocol] Lock-in with {TargetNodeId} rejected with status '{Status}'. Skipping.", targetNodeId, initStatus);
                            await Task.Delay(500, ct);
                            continue;
                        }
                    }

                    // Step 2: Pairwise node swap & signature exchange
                    _logger.LogInformation("[Consensus Protocol] Engaged target node {TargetNodeId} in pairwise merge step.", targetNodeId);

                    bool swapOk = await ExecutePairwiseSwapAsync(targetNodeId, session, ct);
                    LogConsensusEvent("merge", new Dictionary<string, object?> { ["peer"] = targetNodeId, ["ok"] = swapOk });
                    if (swapOk)
                    {
                        await RequestAndAttachSyncSetSignatureAsync(targetNodeId, session, ct);
                        _sessionManager.MarkNodeAsVisited(targetNodeId);
                    }
                    else
                    {
                        _sessionManager.RequeueNodeForRetry(targetNodeId, MaxConnectRetries);
                    }

                    _sessionManager.SetActiveTargetNode(null);

                    // Step 3: Ingest pending deltas, generate local delta, finalize iteration
                    await IngestPendingDeltasAsync(session);
                    await CreateAndBroadcastLocalDeltaAsync(session, ct);
                    await FinalizeIterationAndReportAsync(session);

                    await Task.Delay(250, ct);
                }

                _logger.LogInformation("[Consensus Protocol] Visit queue empty. Entering post-expansion idling state.");
                LogConsensusEvent("queue_empty", new Dictionary<string, object?> { ["known"] = KnownPeerCount() });
                await SyncMergeDirToConsensusStoreAsync(session);

                while (_sessionManager.IsBusy() && !ct.IsCancellationRequested)
                {
                    if (IsMidnightBoundaryReached())
                    {
                        _logger.LogWarning("[Consensus Protocol] Midnight UTC boundary reached during idling phase. Finalizing session.");
                        await CompleteAndFinalizeSessionAsync("midnight");
                        return;
                    }

                    if (_sessionManager.HasNodesToVisit() && string.IsNullOrEmpty(session.TargetNodeId))
                    {
                        _logger.LogInformation("[Consensus Protocol] New nodes queued while idling. Resuming visits.");
                        LogConsensusEvent("resume", new Dictionary<string, object?> { ["known"] = KnownPeerCount() });
                        break;
                    }

                    await EchoRoundAsync(session, ct);
                    await CorroborateAsync(session, ct);

                    if (_sessionManager.HasPendingDeltas())
                    {
                        _logger.LogInformation("[Consensus Protocol] Pending deltas detected during idling. Ingesting...");
                        await IngestPendingDeltasAsync(session);
                        await CreateAndBroadcastLocalDeltaAsync(session, ct);
                        await FinalizeIterationAndReportAsync(session);
                        await SyncMergeDirToConsensusStoreAsync(session);
                    }

                    if (DateTime.UtcNow - session.LastActivityUtc >= TimeSpan.FromMinutes(5))
                    {
                        _logger.LogInformation("[Consensus Protocol] Inactivity threshold (5m) reached. Finalizing consensus.");
                        await BroadcastConsensusFinishedAsync(session);
                        await CompleteAndFinalizeSessionAsync("inactivity");
                        return;
                    }

                    await Task.Delay(3000, ct);
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("[Consensus Protocol] Consensus loop canceled.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Consensus Protocol] Error in consensus workflow loop.");
            await CompleteAndFinalizeSessionAsync("error");
        }
    }


    private async Task<string> HandleIncomingStartPingAsync(string senderNodeId, P2PPacketEnvelope frame, CancellationToken ct)
    {
        // The start ping must be signed by the node that sends it: the cost of starting a consensus lands on
        // exactly that node, and nobody else can start one in its name.
        var (verdict, refusal) = await CheckSignedRequestAsync(senderNodeId, frame, "start ping");
        if (refusal != null) return refusal;

        var session = _sessionManager.GetActiveSession();

        if (session != null)
        {
            if (string.Equals(session.TargetNodeId, senderNodeId, StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrEmpty(session.TargetNodeId))
            {
                return "AVAILABLE";
            }
            return "BUSY";
        }

        _logger.LogInformation("[Consensus Protocol] Received signed ConsensusStartPing from {SenderNodeId}{NewKey}. Debuffing its rating (it starts the consensus) and starting session...",
            senderNodeId, verdict == SignedRequestVerdict.OkNewKey ? " (on a new key its earlier key vouches for)" : string.Empty);
        await _ratingService.ApplyLocalReputationUpdateAsync(senderNodeId, -5);

        var newSession = await _sessionManager.TryAcquireSessionAsync(_identity.NodeId);
        if (newSession != null)
        {
            SeedVisitQueueFromPeerTracker();
            RegisterConsensusNetworkHandlers();
            LogConsensusEvent("start", new Dictionary<string, object?> { ["role"] = "joined", ["from"] = senderNodeId, ["known"] = KnownPeerCount(), ["bytes"] = TrafficCounter.ConsensusBytes, ["msgs"] = TrafficCounter.ConsensusMessages });

            _sessionLoopCts?.Cancel();
            _sessionLoopCts = new CancellationTokenSource();

            _ = Task.Run(() => ExecuteConsensusWorkflowLoopAsync(_sessionLoopCts.Token), ct);
            return "STARTING_UP";
        }

        return "BUSY";
    }

    private async Task<string> SendStartPingAsync(string targetNodeId, CancellationToken ct)
    {
        try
        {
            var myReq = BuildSignedRequest(targetNodeId);

            var result = await _routerService.SendRequestAsync(
                targetNodeId,
                "ConsensusStartPing",
                JsonSerializer.Serialize(myReq),
                new RouteSettings { Timeout = TimeSpan.FromSeconds(15), CancellationToken = ct });

            if (result.IsSuccess && !string.IsNullOrEmpty(result.Payload))
            {
                return result.Payload.Trim().Trim('"');
            }

            return "NO_RESPONSE";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Consensus Protocol] Start ping to {TargetNodeId} failed.", targetNodeId);
            return "ERROR";
        }
    }

    private async Task<string> SendMergeInitRequestAsync(string targetNodeId, CancellationToken ct)
    {
        try
        {
            var myReq = BuildSignedRequest(targetNodeId, HeldNodeIds(_sessionManager.GetActiveSession()).ToList());

            var result = await _routerService.SendRequestAsync(
                targetNodeId,
                "ConsensusMergeInitRequest",
                JsonSerializer.Serialize(myReq),
                new RouteSettings { Timeout = TimeSpan.FromSeconds(15), CancellationToken = ct });

            if (result.IsSuccess && !string.IsNullOrEmpty(result.Payload))
            {
                return result.Payload.Trim().Trim('"');
            }

            return "NO_RESPONSE";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Consensus Protocol] Merge init request to {TargetNodeId} failed.", targetNodeId);
            return "ERROR";
        }
    }

    private async Task<string> HandleIncomingMergeInitRequestAsync(string senderNodeId, P2PPacketEnvelope frame, CancellationToken ct)
    {
        var session = _sessionManager.GetActiveSession();
        if (session == null) return "NO_SESSION_WTF";

        var (_, refusal) = await CheckSignedRequestAsync(senderNodeId, frame, "merge init");
        if (refusal != null) return refusal;
        List<string>? senderHeld = null;
        try { senderHeld = JsonSerializer.Deserialize<ConsensusRequest>(frame.SerializedPayload!)?.HeldNodeIds; } catch { }
        foreach (var id in senderHeld ?? new List<string>()) session.KnownIds.TryAdd(id, 0);

        if (!string.IsNullOrEmpty(session.TargetNodeId) && !string.Equals(session.TargetNodeId, senderNodeId, StringComparison.OrdinalIgnoreCase))
        {
            return $"WTF_BUSY|{session.TargetNodeId}";
        }

        // Only when we really hold the sender's updates (merged with it, or its delta reached us).
        // Having its record in a sync set is not enough.
        if (session.VisitedNodes.Contains(senderNodeId))
        {
            // We hold its updates. Does it hold ours? Only then is there nothing to swap.
            var lacking = HeldNodeIds(session);
            lacking.ExceptWith(senderHeld ?? new List<string>());
            lacking.Remove(senderNodeId); lacking.Remove(_identity.NodeId);
            if (senderHeld == null || lacking.Count == 0) return "ALREADY_SYNCED";
            _logger.LogInformation("[Consensus Protocol] {SenderNodeId} already reached us but lacks {Count} node(s) we hold ({Nodes}); telling it to pull them.",
                senderNodeId, lacking.Count, string.Join(", ", lacking.Take(8)));
            return "ALREADY_SYNCED|" + string.Join(",", lacking.Take(MaxPullIds));
        }

        _sessionManager.SetActiveTargetNode(senderNodeId);
        _logger.LogInformation("[Consensus Protocol] Accepted lock-in init request from {SenderNodeId}.", senderNodeId);

        return "OK_AVAILABLE";
    }


    #region Signed consensus requests

    private readonly ConcurrentDictionary<string, DateTime> _seenRequestNonces = new(StringComparer.Ordinal);
    private static readonly TimeSpan RequestMaxAge = TimeSpan.FromMinutes(5);

    /// <summary>Node ids in this node's merge so far (milestone + current iteration).</summary>
    private static HashSet<string> HeldNodeIds(ConsensusMergeSession? session)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var op = session?.StagingOperation;
        if (op == null) return set;
        foreach (var dir in new[] { op.MergeDir, op.IterDir })
        {
            try
            {
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                    foreach (var f in Directory.GetFiles(dir, "*.json")) set.Add(Path.GetFileNameWithoutExtension(f));
            }
            catch { }
        }
        return set;
    }

    private ConsensusRequest BuildSignedRequest(string targetNodeId, List<string>? heldNodeIds = null)
    {
        var keys = _identity.GetDayKeys();
        var identity = _identity.GetImmutableData();
        var request = new ConsensusRequest
        {
            NodeIdFrom = _identity.NodeId,
            NodeIdTo = targetNodeId,
            TimeStamp = DateTime.UtcNow,
            NonTransitive = true,
            Nonce = Guid.NewGuid().ToString("N"),
            SenderIdentity = identity,
            HeldNodeIds = heldNodeIds ?? new List<string>(),
        };

        // Earlier day keys vouch for today's identity, so a receiver that only knows an older key sees the change is ours
        byte[] identityHash = identity.GetNodeDataHash();
        string today = DateTime.UtcNow.ToString("yyyy-MM-dd");
        foreach (var day in _identity.GetRecentDayKeys(4))
        {
            if (day == today) continue;
            var oldKey = _identity.GetPrivateCertificate(day);
            if (oldKey == null || oldKey.Length == 0) continue;
            try { request.KeyLineage.Add(new SignatureEnvelope { DayKey = day, Signature = P2PCrypto.SignData(identityHash, oldKey) }); } catch { }
            if (request.KeyLineage.Count >= 2) break;
        }

        request.Signature = P2PCrypto.SignData(request.GetHash(), keys.PrivateSignatureKey);
        return request;
    }

    /// <summary>
    /// Checks a signed consensus request and applies the penalty a discrepancy calls for. Returns the reply to send
    /// back when the request is refused, or null when it may go on.
    /// </summary>
    private async Task<(SignedRequestVerdict Verdict, string? Refusal)> CheckSignedRequestAsync(string senderNodeId, P2PPacketEnvelope frame, string what)
    {
        ConsensusRequest? req = null;
        try
        {
            if (!string.IsNullOrEmpty(frame.SerializedPayload))
                req = JsonSerializer.Deserialize<ConsensusRequest>(frame.SerializedPayload);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[Consensus Protocol] Unreadable {What} from {SenderNodeId}.", what, senderNodeId);
        }

        var verdict = await VerifySignedRequestAsync(senderNodeId, req);
        switch (verdict)
        {
            case SignedRequestVerdict.Ok:
            case SignedRequestVerdict.OkNewKey:
                _evil.OnSignedRequest(senderNodeId, what == "start ping" ? "ConsensusStartPing" : "ConsensusMergeInitRequest", frame.SerializedPayload ?? string.Empty);
                return (verdict, null);

            case SignedRequestVerdict.Unsigned:
                // Nothing proves who sent it, so nobody is charged for it either; it simply does not count
                _logger.LogWarning("[Anti-Tamper Guard] Unsigned {What} from {SenderNodeId} refused.", what, senderNodeId);
                return (verdict, "FUCK_UNSIGNED");

            case SignedRequestVerdict.Replay:
                _logger.LogWarning("[Anti-Tamper Guard] {What} from {SenderNodeId} is stale or a repeat; refused.", what, senderNodeId);
                await _ratingService.ApplyLocalReputationUpdateAsync(senderNodeId, -10);
                return (verdict, "FUCK_TIMESTAMP");

            case SignedRequestVerdict.WrongSender:
            case SignedRequestVerdict.WrongTarget:
                _logger.LogWarning("[Anti-Tamper Guard] {What} arriving from {SenderNodeId} was signed by {From} for {To}: passed on from elsewhere. Refused; evidence kept.",
                    what, senderNodeId, req?.NodeIdFrom, req?.NodeIdTo);
                await _ratingService.ApplyLocalReputationUpdateAsync(senderNodeId, -10);
                SaveRequestEvidence(verdict, senderNodeId, req, what);
                return (verdict, "FUCK_NOT_YOURS");

            case SignedRequestVerdict.KeyChangeUnproven:
                _logger.LogWarning("[Anti-Tamper Guard] {What} from {SenderNodeId} is signed with a key we have not seen for it, and nothing shows {SenderNodeId} changed keys (no earlier key vouches for it). Refused; evidence kept.",
                    what, senderNodeId, senderNodeId);
                await _ratingService.ApplyLocalReputationUpdateAsync(senderNodeId, -15);
                SaveRequestEvidence(verdict, senderNodeId, req, what);
                return (verdict, "FUCK_YOU_ARE");

            default:
                _logger.LogWarning("[Anti-Tamper Guard] Signature verification failed on {What} from {SenderNodeId}. Refused; evidence kept.", what, senderNodeId);
                await _ratingService.ApplyLocalReputationUpdateAsync(senderNodeId, -15);
                SaveRequestEvidence(verdict, senderNodeId, req, what);
                return (verdict, "FUCK_YOU_ARE");
        }
    }

    private async Task<SignedRequestVerdict> VerifySignedRequestAsync(string senderNodeId, ConsensusRequest? req)
    {
        if (req == null || req.Signature == null || req.Signature.Length == 0 || string.IsNullOrEmpty(req.NodeIdFrom))
            return SignedRequestVerdict.Unsigned;

        if (!string.Equals(req.NodeIdFrom, senderNodeId, StringComparison.OrdinalIgnoreCase))
            return SignedRequestVerdict.WrongSender;
        if (!string.IsNullOrEmpty(req.NodeIdTo) && !string.Equals(req.NodeIdTo, _identity.NodeId, StringComparison.OrdinalIgnoreCase))
            return SignedRequestVerdict.WrongTarget;

        var now = DateTime.UtcNow;
        if ((now - req.TimeStamp).Duration() > RequestMaxAge)
            return SignedRequestVerdict.Replay;

        byte[] hash = req.GetHash();
        var known = await KnownVerifyKeysAsync(senderNodeId);

        bool signatureChecks = false;
        foreach (var (_, key) in known)
        {
            if (P2PCrypto.TestSignature(hash, key, req.Signature)) { signatureChecks = true; break; }
        }

        var verdict = SignedRequestVerdict.Ok;
        if (!signatureChecks)
        {
            // Not one of the keys we know: is it the key the request itself names, and did the node move to it?
            var identity = req.SenderIdentity;
            bool identityHolds = identity != null &&
                                 string.Equals(identity.NodeId, senderNodeId, StringComparison.OrdinalIgnoreCase) &&
                                 identity.PublicVerifyKey?.Length > 0 && identity.Signature?.Length > 0 &&
                                 P2PCrypto.TestSignature(identity.GetNodeDataHash(), identity.PublicVerifyKey, identity.Signature) &&
                                 P2PCrypto.TestSignature(hash, identity.PublicVerifyKey, req.Signature);
            if (!identityHolds) return SignedRequestVerdict.BadSignature;

            if (known.Count > 0)
            {
                byte[] identityHash = identity!.GetNodeDataHash();
                bool vouched = req.KeyLineage?.Any(env => env?.Signature?.Length > 0 &&
                                   known.Any(k => P2PCrypto.TestSignature(identityHash, k.Key, env.Signature))) == true;
                if (!vouched) return SignedRequestVerdict.KeyChangeUnproven;
            }
            // known.Count == 0: a node we have not met - taken in on its own signed identity
            verdict = SignedRequestVerdict.OkNewKey;
        }

        // Only a correctly signed request is remembered (an unsigned one must not block the real one)
        foreach (var old in _seenRequestNonces.Where(kv => now - kv.Value > RequestMaxAge + RequestMaxAge).Select(kv => kv.Key).ToList())
            _seenRequestNonces.TryRemove(old, out _);
        if (!_seenRequestNonces.TryAdd($"{req.NodeIdFrom}|{req.Nonce}", now))
            return SignedRequestVerdict.Replay;

        return verdict;
    }

    /// <summary>Every verify key we hold for a node: tracker, recent consensus days, and the running merge.</summary>
    private async Task<List<(string Day, byte[] Key)>> KnownVerifyKeysAsync(string nodeId)
    {
        var keys = new List<(string Day, byte[] Key)>();
        void Add(string day, byte[]? key)
        {
            if (key == null || key.Length == 0) return;
            if (keys.Any(k => k.Key.AsSpan().SequenceEqual(key))) return;
            keys.Add((day, key));
        }

        var trackerIdentity = _peerTracker.GetPeer(nodeId)?.Identity;
        if (trackerIdentity != null) Add(trackerIdentity.SignatureTime.ToString("yyyy-MM-dd"), trackerIdentity.PublicVerifyKey);

        for (int i = 0; i < 7; i++)
        {
            string day = DateTime.UtcNow.AddDays(-i).ToString("yyyy-MM-dd");
            try { Add(day, _consensusStore.GetHistoricalPeer(day, nodeId)?.Identity?.PublicVerifyKey); } catch { }
        }

        var staging = _sessionManager.GetActiveSession()?.StagingOperation;
        if (staging != null)
        {
            try { Add(staging.TargetDate, (await _mergeEngine.GetNodeFromStagingHierarchyAsync(staging, nodeId))?.Identity?.PublicVerifyKey); } catch { }
        }

        return keys;
    }

    private void SaveRequestEvidence(SignedRequestVerdict verdict, string senderNodeId, ConsensusRequest? req, string what)
    {
        string today = DateTime.UtcNow.ToString("yyyy-MM-dd");
        string basePath = _p2pSettings.CurrentValue.DataStoragePath ?? "./Module_Data/P2P";
        var evidence = new ConsensusEvidence
        {
            Kind = "request-" + verdict.ToString().ToLowerInvariant(),
            SubjectNodeId = senderNodeId,
            ConsensusDate = today,
            DetectedByNodeId = _identity.NodeId,
            Reason = $"{what}: {verdict}",
            Request = req,
            PenalizedNodeIds = new List<string> { senderNodeId },
        };
        if (req?.SenderIdentity != null)
            evidence.Versions.Add(new EvidenceVersion { Identity = req.SenderIdentity, Sources = new List<string> { senderNodeId } });
        ConsensusEvidenceStore.Write(Path.Combine(basePath, "consensus_evidence", today), evidence, _logger);
    }

    #endregion

    private async Task<bool> ExecutePairwiseSwapAsync(string targetNodeId, ConsensusMergeSession session, CancellationToken ct)
    {
        try
        {
            bool hasReceivedData = false, hadError = false;
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            // The router hands every chunk (and the final "completed" signal) to its own task, in no particular
            // order, and staging ran fire-and-forget: the swap counted as done while chunks were still being
            // written to IterDir and their nodes registered in the sync set. The local delta was then computed
            // and the iteration finalized without them (their nodes landed in the NEXT iteration's sync set, so
            // the next delta's "old nodes" list was empty). Now the swap ends only after every chunk is staged.
            var swapGate = new object();
            int stagedChunks = 0;
            int expectedChunks = 0;      // 0 = not known (older sender): finish on the completed signal
            bool senderDone = false;
            void TryFinishSwap()
            {
                lock (swapGate)
                {
                    if (senderDone && (expectedChunks <= 0 || stagedChunks >= expectedChunks))
                        tcs.TrySetResult(hasReceivedData);
                }
            }

            await _routerService.SendRequestProgressiveAsync(
                targetNodeId,
                "ConsensusSwap",
                string.Empty,
                (payloadJson, error, progress, isCompleted) =>
                {
                    if (!string.IsNullOrEmpty(error))
                    {
                        _logger.LogWarning("[Consensus Protocol] Progressive swap segment error from {Target}: {Error}", targetNodeId, error);
                        hadError = true;
                        if (isCompleted) tcs.TrySetResult(hasReceivedData);
                        return;
                    }

                    bool chunkCounted = false;

                    if (!string.IsNullOrEmpty(payloadJson))
                    {
                        try
                        {
                            var chunk = JsonSerializer.Deserialize<ConsensusNodeSwapPayload>(payloadJson);
                            if (chunk != null && chunk.ChunkCount > 0)
                            {
                                lock (swapGate) expectedChunks = chunk.ChunkCount;
                            }

                            if (chunk?.Nodes != null && chunk.Nodes.Count > 0)
                            {
                                hasReceivedData = true;
                                chunkCounted = true;
                                _ = Task.Run(async () =>
                                {
                                  try
                                  {
                                    await _mergeEngine.StageIncomingNodeSetAsync(session.StagingOperation!, targetNodeId, chunk.Nodes);

                                    var nodeIds = new List<string>();
                                    foreach (var node in chunk.Nodes)
                                    {
                                        if (node?.Identity?.NodeId != null)
                                        {
                                            nodeIds.Add(node.Identity.NodeId);
                                            await _peerTracker.UpdatePeerWasSeenByAsync(node.Identity.NodeId, targetNodeId);
                                        }
                                    }

                                    QueueNeighboursToVisit(nodeIds);
                                    _sessionManager.RegisterNodesInCurrentSyncSet(nodeIds);
                                    foreach (var id in nodeIds) session.KnownIds.TryAdd(id, 0);
                                  }
                                  catch (Exception ex)
                                  {
                                    _logger.LogError(ex, "[Consensus Protocol] Error staging swap chunk from {TargetNodeId}.", targetNodeId);
                                  }
                                  finally
                                  {
                                    lock (swapGate) stagedChunks++;
                                    TryFinishSwap();
                                  }
                                });
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "[Consensus Protocol] Error processing incoming node swap chunk from {TargetNodeId}.", targetNodeId);
                        }
                    }

                    if (!chunkCounted && !string.IsNullOrEmpty(payloadJson))
                    {
                        lock (swapGate) stagedChunks++;   // empty or unreadable chunk: nothing to stage, but it counts
                    }

                    if (isCompleted)
                    {
                        lock (swapGate) senderDone = true;
                    }
                    TryFinishSwap();
                },
                new RouteSettings { Timeout = TimeSpan.FromSeconds(20), CancellationToken = ct }
            );

            // Safety net: a lost chunk must not hang the session
            var finished = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(60), ct));
            if (finished != tcs.Task)
            {
                _logger.LogWarning("[Consensus Protocol] Swap with {TargetNodeId}: only {Staged}/{Expected} chunks staged after 60 s; continuing.",
                    targetNodeId, stagedChunks, expectedChunks);
                return hasReceivedData;
            }
            bool received = await tcs.Task;
            if (!received && !hadError) await NoteWithholdingAsync(targetNodeId, session);   // a clean, complete, empty answer
            return received;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Consensus Protocol] Exception during pairwise node swap with {TargetNodeId}.", targetNodeId);
            return false;
        }
    }

    private async Task RequestAndAttachSyncSetSignatureAsync(string targetNodeId, ConsensusMergeSession session, CancellationToken ct)
    {
        try
        {
            var fullChain = _sessionManager.GetSyncSetSignatures();
            // Point at the newest link of the highest order; the new link is one order above it.
            // (Count + 1 skipped orders as soon as two merges had produced links of the same order.)
            var prevSig = fullChain
                .OrderBy(x => x.Order)
                .ThenBy(x => x.TimestampUtc)
                .LastOrDefault();
            int order = (prevSig?.Order ?? 0) + 1;
            long timestamp = DateTime.UtcNow.Ticks;

            byte[] localSigBytes = ComputeChainLinkSignature(
                prevSig?.Signature, order, _identity.NodeId, targetNodeId, timestamp, session.DayKeySignatureKey);

            var localChainLink = new ChainSignature
            {
                Order = order,
                SignerNodeId = _identity.NodeId,
                TargetNodeId = targetNodeId,
                TimestampUtc = new DateTime(timestamp, DateTimeKind.Utc),
                Signature = localSigBytes
            };

            var outgoingChain = new List<ChainSignature>(fullChain) { localChainLink };

            var reqPayload = new ConsensusSignatureRequestPayload
            {
                RequesterNodeId = _identity.NodeId,
                TargetNodeId = targetNodeId,
                ExchangedNodeIds = session.CurrentSyncSet.ToList(),
                SignatureOrder = order,
                PreviousSignature = prevSig,
                FullSignatureChain = outgoingChain
            };

            var result = await _routerService.SendRequestAsync(
                targetNodeId,
                "ConsensusSignatureRequest",
                JsonSerializer.Serialize(reqPayload),
                new RouteSettings { Timeout = TimeSpan.FromSeconds(15), CancellationToken = ct });

            if (result.IsSuccess && !string.IsNullOrEmpty(result.Payload))
            {
                if (result.Payload.StartsWith("FUCK", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning("[Anti-Tamper Guard] Signature exchange rejected by target {TargetNodeId}: {Status}", targetNodeId, result.Payload);
                    await _ratingService.ApplyLocalReputationUpdateAsync(targetNodeId, -5);
                    return;
                }

                var responseSig = JsonSerializer.Deserialize<ChainSignature>(result.Payload);
                if (responseSig != null && responseSig.Signature.Length > 0)
                {
                    responseSig.SignerNodeId = targetNodeId;
                    responseSig.TargetNodeId = _identity.NodeId;
                    _sessionManager.AddSyncSetSignature(responseSig);
                }
            }

            _sessionManager.AddSyncSetSignature(localChainLink);

            foreach (var nodeId in session.CurrentSyncSet)
            {
                await _peerTracker.UpdatePeerWasSeenByAsync(nodeId, targetNodeId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Consensus Protocol] Failed exchanging signatures with {TargetNodeId}.", targetNodeId);
        }
    }

    private async Task<string> HandleIncomingNodeSwapAsync(string senderNodeId, P2PPacketEnvelope frame, CancellationToken ct)
    {
        var session = _sessionManager.GetActiveSession();
        if (session == null)
        {
            await _routerService.SendProgressiveResponseAsync(senderNodeId, frame.MessageId, "ConsensusSwap", string.Empty, 100, true, "NO_SESSION_WTF");
            return null!;
        }

        var localNodes = _evil.TamperOutgoingNodes(senderNodeId, await LoadLocalNodesForSwapAsync(session));
        int totalNodes = localNodes.Count;

        if (totalNodes == 0)
        {
            var emptyPayload = new ConsensusNodeSwapPayload { ChunkIndex = 0, ChunkCount = 1 };
            await _routerService.SendProgressiveResponseAsync(
                senderNodeId, frame.MessageId, "ConsensusSwap", JsonSerializer.Serialize(emptyPayload), 100, true);
            return null!;
        }

        int chunkCount = (totalNodes + NodeSwapBatchSize - 1) / NodeSwapBatchSize;
        for (int i = 0; i < totalNodes; i += NodeSwapBatchSize)
        {
            var batch = localNodes.Skip(i).Take(NodeSwapBatchSize).ToList();
            var payload = new ConsensusNodeSwapPayload { Nodes = batch, ChunkIndex = i / NodeSwapBatchSize, ChunkCount = chunkCount };

            int processedCount = Math.Min(i + NodeSwapBatchSize, totalNodes);
            double progress = ((double)processedCount / totalNodes) * 100.0;
            bool isLast = (processedCount >= totalNodes);

            await _routerService.SendProgressiveResponseAsync(
                senderNodeId, frame.MessageId, "ConsensusSwap", JsonSerializer.Serialize(payload), progress, isLast);
        }

        return null!;
    }

    private async Task<string> HandleIncomingSignatureRequestAsync(string senderNodeId, P2PPacketEnvelope frame, CancellationToken ct)
    {
        var session = _sessionManager.GetActiveSession();
        if (session == null || string.IsNullOrEmpty(frame.SerializedPayload))
        {
            return "NO_SESSION_WTF";
        }

        try
        {
            var req = JsonSerializer.Deserialize<ConsensusSignatureRequestPayload>(frame.SerializedPayload);
            if (req == null)
            {
                await _ratingService.ApplyLocalReputationUpdateAsync(senderNodeId, -5);
                return "FUCK_THIS_REQUEST";
            }

            if (req.ExchangedNodeIds != null && req.ExchangedNodeIds.Count > 0)
            {
                QueueNeighboursToVisit(req.ExchangedNodeIds);
                _sessionManager.RegisterNodesInCurrentSyncSet(req.ExchangedNodeIds);
                foreach (var id in req.ExchangedNodeIds) session.KnownIds.TryAdd(id, 0);
                foreach (var nodeId in req.ExchangedNodeIds)
                {
                    await _peerTracker.UpdatePeerWasSeenByAsync(nodeId, senderNodeId);
                }
            }

            if (req.FullSignatureChain != null && req.FullSignatureChain.Count > 0)
            {
                foreach (var sig in req.FullSignatureChain)
                {
                    if (sig != null) _sessionManager.AddSyncSetSignature(sig);
                }
            }

            // Our link points at the requester's own link for this merge (order n -> n+1),
            // not at the link the requester itself pointed at - otherwise it never verifies.
            var requesterLink = req.FullSignatureChain?
                .LastOrDefault(s => s != null &&
                                    s.Order == req.SignatureOrder &&
                                    string.Equals(s.SignerNodeId, senderNodeId, StringComparison.OrdinalIgnoreCase));
            int order = req.SignatureOrder + 1;
            long timestamp = DateTime.UtcNow.Ticks;
            byte[] targetSigBytes = ComputeChainLinkSignature(
                requesterLink?.Signature ?? req.PreviousSignature?.Signature, order, _identity.NodeId, senderNodeId, timestamp, session.DayKeySignatureKey);

            var returnSignature = new ChainSignature
            {
                Order = order,
                SignerNodeId = _identity.NodeId,
                TargetNodeId = senderNodeId,
                TimestampUtc = new DateTime(timestamp, DateTimeKind.Utc),
                Signature = targetSigBytes
            };

            _sessionManager.AddSyncSetSignature(returnSignature);

            // The swap is complete on our side too: we hold the sender's updates now.
            _sessionManager.MarkNodeAsVisited(senderNodeId);
            LogConsensusEvent("merge_in", new Dictionary<string, object?> { ["peer"] = senderNodeId, ["order"] = returnSignature.Order });

            return JsonSerializer.Serialize(returnSignature);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Consensus Protocol] Error handling signature request from {SenderNodeId}.", senderNodeId);
            await _ratingService.ApplyLocalReputationUpdateAsync(senderNodeId, -5);
            return "FUCK_SIGNATURE";
        }
    }


    private async Task<string> HandleIncomingDeltaPingAsync(string senderNodeId, P2PPacketEnvelope frame, CancellationToken ct)
    {
        var session = _sessionManager.GetActiveSession();
        if (session == null || string.IsNullOrEmpty(frame.SerializedPayload)) return "NO_SESSION_WTF";

        try
        {
            var ping = JsonSerializer.Deserialize<ConsensusDeltaPing>(frame.SerializedPayload);
            if (ping == null || string.IsNullOrEmpty(ping.DeltaIdentifier)) return "FUCK_THIS_REQUEST";

            string remoteDeltaId = ping.DeltaIdentifier;
            session.KnownIds.TryAdd(senderNodeId, 0);
            foreach (var id in ping.Nodes ?? new HashSet<string>()) session.KnownIds.TryAdd(id, 0);
            foreach (var s in ping.SignatureChain ?? new List<ChainSignature>()) if (!string.IsNullOrEmpty(s?.SignerNodeId)) session.KnownIds.TryAdd(s.SignerNodeId, 0);

            if (session.LocalDeltas.ContainsKey(remoteDeltaId) || session.PendingDeltasToMerge.ContainsKey(remoteDeltaId))
                return "ALREADY_SYNCED";

            if (_sessionManager.PrepareDownloadedDeltaFolder(out string assignedDeltaId, out string destFolderPath))
            {
                bool downloaded = await DownloadDeltaFilesAsync(senderNodeId, remoteDeltaId, destFolderPath, ct);
                if (downloaded)
                {
                    var registration = await _sessionManager.RegisterDownloadedDeltaAsync(senderNodeId, destFolderPath, ping.SignatureChain);
                    if (registration == DeltaRegistration.Registered)
                    {
                        _logger.LogInformation("[Consensus Protocol] Successfully downloaded and registered delta {DeltaId} from {Sender}.", assignedDeltaId, senderNodeId);

                        // Their updates reached us through the delta: no need to visit them ourselves.
                        _sessionManager.MarkNodeAsVisited(senderNodeId);
                        LogConsensusEvent("delta", new Dictionary<string, object?>
                        {
                            ["from"] = senderNodeId,
                            ["signers"] = ping.SignatureChain?.Where(s => s != null).Select(s => s.SignerNodeId).Distinct().ToList(),
                            ["unconfirmed"] = _sessionManager.GetUnconfirmedSigners(assignedDeltaId),
                        });
                        if (ping.SignatureChain != null)
                        {
                            // A signer whose link we could not check (no key yet) has not been confirmed: not visited
                            var unconfirmed = _sessionManager.GetUnconfirmedSigners(assignedDeltaId);
                            foreach (var sig in ping.SignatureChain)
                            {
                                if (!string.IsNullOrEmpty(sig?.SignerNodeId) &&
                                    !unconfirmed.Contains(sig.SignerNodeId, StringComparer.OrdinalIgnoreCase) &&
                                    !string.Equals(sig.SignerNodeId, _identity.NodeId, StringComparison.OrdinalIgnoreCase))
                                {
                                    _sessionManager.MarkNodeAsVisited(sig.SignerNodeId);
                                }
                            }
                        }
                    }
                    else if (registration == DeltaRegistration.InvalidSignature)
                    {
                        // Only a signature that fails against a key we DO know is evidence of tampering
                        _logger.LogWarning("[Anti-Tamper Guard] Downloaded delta {DeltaId} from {Sender} failed signature validation. Applying reputation penalty.", assignedDeltaId, senderNodeId);
                        await _ratingService.ApplyLocalReputationUpdateAsync(senderNodeId, -15);
                    }
                    // Failed: skipped, logged by the session manager, no penalty. Unknown signers no longer block a
                    // delta: it is registered and its nodes stay doubtful until another source confirms them.
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Consensus Protocol] Error processing delta ping from {SenderNodeId}.", senderNodeId);
            await _ratingService.ApplyLocalReputationUpdateAsync(senderNodeId, -5);
            return "FUCK_THIS_REQUEST";
        }

        return "AVAILABLE";
    }

    /// <summary>
    /// Outbound progressive pull: Requests structured ConsensusDeltaDocument segments from target node.
    /// Safely parses PeerNodeHistoric objects without saving raw unparsed content to disk.
    /// </summary>
    private async Task<bool> DownloadDeltaFilesAsync(string senderNodeId, string remoteDeltaId, string destFolderPath, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<bool>();
        var requestObj = new ConsensusDeltaRequest { DeltaIdentifier = remoteDeltaId };

        await _routerService.SendRequestProgressiveAsync(
            senderNodeId,
            "ConsensusDeltaDownload",
            JsonSerializer.Serialize(requestObj),
            (payloadJson, error, progress, isCompleted) =>
            {
                if (!string.IsNullOrEmpty(error))
                {
                    _logger.LogWarning("[Consensus Protocol] Error downloading delta {DeltaId} from {Sender}: {Error}", remoteDeltaId, senderNodeId, error);
                    if (isCompleted) tcs.TrySetResult(false);
                    return;
                }

                if (!string.IsNullOrEmpty(payloadJson))
                {
                    try
                    {
                        var docSegment = JsonSerializer.Deserialize<ConsensusDeltaDocument>(payloadJson);
                        if (docSegment?.DeltaNodes != null && docSegment.DeltaNodes.Count > 0)
                        {
                            var transferredList = new List<PeerNodeTransferred>();
                            string nodesSubDir = Path.Combine(destFolderPath, "nodes");
                            Directory.CreateDirectory(nodesSubDir);

                            foreach (var nodeHistoric in docSegment.DeltaNodes)
                            {
                                if (nodeHistoric?.Identity?.NodeId != null)
                                {
                                    // Save parsed node JSON strictly inside nodes/ subfolder
                                    string nodePath = Path.Combine(nodesSubDir, $"{nodeHistoric.Identity.NodeId}.json");
                                    File.WriteAllText(nodePath, JsonSerializer.Serialize(nodeHistoric, new JsonSerializerOptions { WriteIndented = true }));

                                    transferredList.Add(new PeerNodeTransferred
                                    {
                                        Identity = nodeHistoric.Identity,
                                        Signatures = nodeHistoric.Signatures ?? new List<SignatureEnvelope>(),
                                        SignatureScore = nodeHistoric.SignatureScore,
                                        TrustScore = nodeHistoric.TrustScore,
                                        LastSeen = nodeHistoric.LastSeen
                                    });
                                }
                            }

                            // Not staged here: the delta's signature chain is checked only after the download
                            // (RegisterDownloadedDeltaAsync), and IngestPendingDeltasAsync stages its nodes then.
                            // Staging them straight away let unverified nodes in, staged them twice, and the
                            // fire-and-forget write could land after the session had already deleted its workspace.
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "[Consensus Protocol] Error parsing downloaded delta document chunk.");
                    }
                }

                if (isCompleted)
                {
                    tcs.TrySetResult(true);
                }
            },
            new RouteSettings { Timeout = TimeSpan.FromSeconds(30), CancellationToken = ct }
        );

        return await tcs.Task;
    }

    /// <summary>
    /// Inbound progressive server: Reads requested delta nodes, deserializes them safely into PeerNodeHistoric,
    /// constructs typed ConsensusDeltaDocument payloads, and streams them progressively.
    /// </summary>
    private async Task<string> HandleIncomingDeltaDownloadAsync(string senderNodeId, P2PPacketEnvelope frame, CancellationToken ct)
    {
        var session = _sessionManager.GetActiveSession();
        if (session == null || string.IsNullOrEmpty(frame.SerializedPayload))
        {
            await _routerService.SendProgressiveResponseAsync(senderNodeId, frame.MessageId, "ConsensusDeltaDownload", string.Empty, 100, true, "NO_SESSION_WTF");
            return null!;
        }

        string deltaId = string.Empty;
        try
        {
            var req = JsonSerializer.Deserialize<ConsensusDeltaRequest>(frame.SerializedPayload);
            deltaId = req?.DeltaIdentifier ?? string.Empty;
        }
        catch { }

        if (string.IsNullOrEmpty(deltaId))
        {
            await _routerService.SendProgressiveResponseAsync(senderNodeId, frame.MessageId, "ConsensusDeltaDownload", string.Empty, 100, true, "INVALID_REQUEST");
            return null!;
        }

        var deltaItem = _sessionManager.GetLocalDelta(deltaId);
        if (deltaItem == null || !Directory.Exists(deltaItem.DeltaFolderPath))
        {
            await _routerService.SendProgressiveResponseAsync(senderNodeId, frame.MessageId, "ConsensusDeltaDownload", string.Empty, 100, true, "DELTA_NOT_FOUND");
            return null!;
        }

        string nodesDir = Path.Combine(deltaItem.DeltaFolderPath, "nodes");
        var nodeFiles = Directory.Exists(nodesDir) ? Directory.GetFiles(nodesDir, "*.json").ToList() : new List<string>();
        int totalNodes = nodeFiles.Count;

        if (totalNodes == 0)
        {
            var emptyDoc = new ConsensusDeltaDocument();
            await _routerService.SendProgressiveResponseAsync(
                senderNodeId, frame.MessageId, "ConsensusDeltaDownload", JsonSerializer.Serialize(emptyDoc), 100, true);
            return null!;
        }

        for (int i = 0; i < totalNodes; i += DeltaNodeBatchSize)
        {
            var batchFiles = nodeFiles.Skip(i).Take(DeltaNodeBatchSize).ToList();
            var docNodes = new List<PeerNodeHistoric>();

            foreach (var filePath in batchFiles)
            {
                try
                {
                    string json = await File.ReadAllTextAsync(filePath, ct);
                    PeerNodeHistoric? node = null;
                    node = ConsensusNodeJson.ReadHistoric(json);

                    if (node?.Identity != null)
                    {
                        docNodes.Add(node);
                    }
                }
                catch { }
            }

            var chunkDoc = new ConsensusDeltaDocument { DeltaNodes = docNodes };

            int processedCount = Math.Min(i + DeltaNodeBatchSize, totalNodes);
            double progress = ((double)processedCount / totalNodes) * 100.0;
            bool isLast = (processedCount >= totalNodes);

            await _routerService.SendProgressiveResponseAsync(
                senderNodeId, frame.MessageId, "ConsensusDeltaDownload", JsonSerializer.Serialize(chunkDoc), progress, isLast);
        }

        return null!;
    }


    private async Task CreateAndBroadcastLocalDeltaAsync(ConsensusMergeSession session, CancellationToken ct)
    {
        var deltaItem = await _sessionManager.CreateLocalDeltaAsync();
        if (deltaItem == null) return;

        // Deltas go to OLD nodes (earlier iterations' sync sets) and to our live neighbours: whatever we learned
        // since our last delta is pushed on, so records cross between groups of nodes that never merged with each
        // other (a delta only to old partners stayed inside its own merge tree, and a node whose partners all lacked
        // a record never got it). Not to the nodes of the merge that just happened (they got our full state in the
        // swap), and not back to whoever sent us what this delta contains. It stops by itself: a node that learns
        // nothing new makes no delta.
        var exclude = new HashSet<string>(session.CurrentSyncSet, StringComparer.OrdinalIgnoreCase);
        exclude.UnionWith(session.IterationSources.Keys);
        exclude.Add(_identity.NodeId);
        session.IterationSources.Clear();
        var neighbours = _connectionManager.GetActiveConnections()
            .Where(c => c?.NodeId != null && c.PeerConnection?.connectionState == SIPSorcery.Net.RTCPeerConnectionState.connected)
            .Select(c => c.NodeId);
        var pastNodes = _sessionManager.GetPastSyncSetNodes()
            .Concat(neighbours)
            .Where(id => !exclude.Contains(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (pastNodes.Count == 0) return;

        var ping = new ConsensusDeltaPing
        {
            DeltaFromNode = _identity.NodeId,
            DeltaIdentifier = deltaItem.DeltaId,
            DeltaVerifyKey = session.DayKeyVerifyKey,
            SignatureChain = _evil.TamperChain(_sessionManager.GetSyncSetSignatures()),
            SourceSignatureChain = _sessionManager.GetSyncSetSignatures(),
            // Every node id we hold, not just this merge's: receivers learn which nodes exist and can pull the
            // records they lack (ids only - cheap). With only the sync set, a node whose partners never saw a
            // node simply never knew it existed and finished one short.
            Nodes = new HashSet<string>(session.CurrentSyncSet.Concat(HeldNodeIds(session)), StringComparer.OrdinalIgnoreCase)
        };

        string payload = JsonSerializer.Serialize(ping);
        _logger.LogInformation("[Consensus Protocol] Delta {DeltaId} ({Count} node record(s) changed) pushed to {Targets} old partner(s) and neighbour(s): {Nodes}.",
            deltaItem.DeltaId, session.CurrentSyncSet.Count, pastNodes.Count, string.Join(", ", pastNodes.Take(12)));

        foreach (var nodeId in pastNodes)
        {
            if (nodeId.Equals(_identity.NodeId, StringComparison.OrdinalIgnoreCase)) continue;

            _ = Task.Run(async () =>
            {
                await EnsurePeerConnectionAsync(nodeId);
                await _routerService.SendMessage(nodeId, "ConsensusDeltaPing", payload, new RouteSettings { CancellationToken = ct });
            });
        }
    }

    private async Task BroadcastConsensusFinishedAsync(ConsensusMergeSession session)
    {
        var pastNodes = _sessionManager.GetPastSyncSetNodes();
        string payload = JsonSerializer.Serialize(new { SessionId = session.SessionId, SenderNodeId = _identity.NodeId });

        foreach (var nodeId in pastNodes)
        {
            if (nodeId.Equals(_identity.NodeId, StringComparison.OrdinalIgnoreCase)) continue;

            _ = Task.Run(async () =>
            {
                await EnsurePeerConnectionAsync(nodeId);
                await _routerService.SendMessage(nodeId, "ConsensusFinished", payload);
            });
        }
    }

    private async Task<string> HandleIncomingConsensusFinishedAsync(string senderNodeId, P2PPacketEnvelope frame, CancellationToken ct)
    {
        _logger.LogInformation("[Consensus Protocol] Received ConsensusFinished signal from {SenderNodeId}.", senderNodeId);
        return string.Empty;
    }

    private async Task IngestPendingDeltasAsync(ConsensusMergeSession session)
    {
        var pendingDeltas = _sessionManager.GetPendingDeltasToMerge();
        if (pendingDeltas.Count == 0) return;

        foreach (var delta in pendingDeltas)
        {
            if (!string.IsNullOrEmpty(delta.SourceNodeId)) session.IterationSources.TryAdd(delta.SourceNodeId, 0);
            try
            {
                string deltaNodesDir = Path.Combine(delta.DeltaFolderPath, "nodes");
                if (Directory.Exists(deltaNodesDir))
                {
                    foreach (var file in Directory.GetFiles(deltaNodesDir, "*.json"))
                    {
                        string json = await File.ReadAllTextAsync(file);
                        PeerNodeHistoric? peerHistoric = null;
                        peerHistoric = ConsensusNodeJson.ReadHistoric(json);

                        if (peerHistoric?.Identity?.NodeId != null)
                        {
                            var transferred = new PeerNodeTransferred
                            {
                                Identity = peerHistoric.Identity,
                                Signatures = peerHistoric.Signatures ?? new List<SignatureEnvelope>(),
                                SignatureScore = peerHistoric.SignatureScore,
                                TrustScore = peerHistoric.TrustScore,
                                LastSeen = peerHistoric.LastSeen
                            };
                            await _mergeEngine.StageIncomingNodeSetAsync(session.StagingOperation!, delta.SourceNodeId, new List<PeerNodeTransferred> { transferred });
                        }
                    }
                }

                _sessionManager.RemovePendingDelta(delta.DeltaId, deleteFolder: true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Consensus Protocol] Error ingesting pending delta {DeltaId}.", delta.DeltaId);
                _sessionManager.RemovePendingDelta(delta.DeltaId, deleteFolder: true);
            }
        }
    }

    private async Task SyncMergeDirToConsensusStoreAsync(ConsensusMergeSession session)
    {
        if (session?.StagingOperation == null) return;

        string mergeDir = session.StagingOperation.MergeDir;
        using var gate = await session.StagingOperation.LockAsync();
        if (!Directory.Exists(mergeDir)) return;

        try
        {
            foreach (var file in Directory.GetFiles(mergeDir, "*.json"))
            {
                string nodeId = Path.GetFileNameWithoutExtension(file);
                string json = await File.ReadAllTextAsync(file);
                PeerNodeHistoric? historicPeer = null;
                historicPeer = ConsensusNodeJson.ReadHistoric(json);

                if (historicPeer?.Identity != null && !string.IsNullOrEmpty(nodeId))
                {
                    _consensusStore.UpdateHistoricalPeer(session.TargetSyncDate, nodeId, historicPeer);
                }
            }

            _logger.LogInformation("[Consensus Protocol] Preemptively synced MergeDir baseline nodes into ConsensusStore for date {Date}.", session.TargetSyncDate);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Consensus Protocol] Failed preemptive MergeDir sync to ConsensusStore.");
        }
    }

    private async Task<bool> EnsurePeerConnectionAsync(string targetNodeId)
    {
        var activeConn = _connectionManager.GetConnection(targetNodeId);
        if (activeConn != null && activeConn.PeerConnection != null &&
            activeConn.PeerConnection.connectionState == SIPSorcery.Net.RTCPeerConnectionState.connected)
        {
            return true;
        }

        var peer = _peerTracker.GetPeer(targetNodeId);
        string[] signalingEndpoints = peer?.Identity?.SignalingEndpoints?.ToArray() ?? Array.Empty<string>();

        var newConn = await _connectionManager.ConnectPeerAsync(targetNodeId, signalingEndpoints, "outbound");
        return newConn != null;
    }

    private void SeedVisitQueueFromPeerTracker()
    {
        var allPeers = _peerTracker.GetLastSeenNodes(PeerTracker.DefaultNodeComparer)
            .Where(p => p.Identity?.NodeId != null && !p.Identity.NodeId.Equals(_identity.NodeId, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var trackerCandidates = allPeers
            .Where(p => p.Identity!.Roles.Contains("tracker"))
            .Select(p => p.Identity!.NodeId)
            .ToList();

        if (trackerCandidates.Count > 0)
        {
            // Merge with neighbours only (nodes we hold a live connection to). Everyone else is reached through
            // their deltas and, for single gaps, a pull of the missing records - not by dialling every node (O(n^2)).
            var neighbours = trackerCandidates.Where(IsConnected).ToList();
            if (neighbours.Count == 0)
                neighbours = trackerCandidates.OrderBy(_ => Random.Shared.Next()).Take(ConsensusFanout(trackerCandidates.Count)).ToList();
            _sessionManager.QueueNodesToVisit(neighbours);
            _logger.LogInformation("[Consensus Protocol] Seeded visit queue with {Count} neighbour(s) of {Total} known tracker node(s).", neighbours.Count, trackerCandidates.Count);
        }
        else
        {
            _logger.LogWarning("[Consensus Role] No trackers found in vicinity! Self-promoting local node to 'tracker' to drive fallback loop.");
            _ = PromoteSelfToTrackerAsync();

            var peerCandidates = allPeers.Select(p => p.Identity!.NodeId).ToList();
            _sessionManager.QueueNodesToVisit(peerCandidates);
        }
    }

    /// <summary>
    /// No separate "promoted_tracker" role exists - self-promotion adds the SAME "tracker" role
    /// everyone else already checks for, directly into NodeRoles. IsTemporarilyPromotedTracker is
    /// pure bookkeeping: it only records whether WE were the one who added "tracker" ourselves, so
    /// EnsureSelfDemotedAsync knows it's safe to remove it later - if "tracker" was already present
    /// (the user's own permanent config), this does nothing and never sets the flag, so a later
    /// demote can never strip a role the user explicitly configured.
    /// </summary>
    private async Task PromoteSelfToTrackerAsync()
    {
        var roles = new List<string>(_p2pSettings.CurrentValue.NodeRoles);
        if (roles.Contains("tracker", StringComparer.OrdinalIgnoreCase))
        {
            return; // already a tracker (permanently, via the user's own config) - nothing to self-promote, nothing to later undo
        }

        if (_p2pSettings.CurrentValue.IsTemporarilyPromotedTracker) return; // already self-promoted from an earlier call this round

        roles.Add("tracker");

        using (var scope = _scopeFactory.CreateScope())
        {
            var settingService = scope.ServiceProvider.GetRequiredService<ISettingService>();
            // Both keys in one reload (was two), so listeners see roles + flag change together.
            settingService.SetAutoUpdate(false);
            try
            {
                await settingService.SetAsync(P2PModuleEntryPoint.MODULE_ID, $"{nameof(P2PSettings)}:{nameof(P2PSettings.NodeRoles)}", roles);
                await settingService.SetAsync(P2PModuleEntryPoint.MODULE_ID, $"{nameof(P2PSettings)}:{nameof(P2PSettings.IsTemporarilyPromotedTracker)}", true);
            }
            finally
            {
                settingService.SetAutoUpdate(true);
                settingService.ForceUpdateOptionsMonitor();
            }
        }

        _identity.TriggerUpdate();
        _logger.LogInformation("[Consensus Role] Local node self-promoted to 'tracker' (temporary - IsTemporarilyPromotedTracker=true).");
    }

    /// <summary>Only ever removes "tracker" if WE added it (IsTemporarilyPromotedTracker=true) - never touches a permanently user-configured one.</summary>
    private async Task EnsureSelfDemotedAsync()
    {
        if (!_p2pSettings.CurrentValue.IsTemporarilyPromotedTracker) return; // nothing to undo

        var roles = new List<string>(_p2pSettings.CurrentValue.NodeRoles);
        roles.RemoveAll(r => string.Equals(r, "tracker", StringComparison.OrdinalIgnoreCase));

        using (var scope = _scopeFactory.CreateScope())
        {
            var settingService = scope.ServiceProvider.GetRequiredService<ISettingService>();
            // Both keys in one reload (was two), so listeners see roles + flag change together.
            settingService.SetAutoUpdate(false);
            try
            {
                await settingService.SetAsync(P2PModuleEntryPoint.MODULE_ID, $"{nameof(P2PSettings)}:{nameof(P2PSettings.NodeRoles)}", roles);
                await settingService.SetAsync(P2PModuleEntryPoint.MODULE_ID, $"{nameof(P2PSettings)}:{nameof(P2PSettings.IsTemporarilyPromotedTracker)}", false);
            }
            finally
            {
                settingService.SetAutoUpdate(true);
                settingService.ForceUpdateOptionsMonitor();
            }
        }

        _identity.TriggerUpdate();
        _logger.LogInformation("[Consensus Role] Self-demoted from temporary 'tracker' role (IsTemporarilyPromotedTracker=false).");
    }

    // An accepted merge that brings no records at all: a node in a session always holds at least the peers it
    // knows, so an empty swap is withholding. Once is a hiccup; the second time it is reported.
    private readonly ConcurrentDictionary<string, int> _withholdStrikes = new(StringComparer.OrdinalIgnoreCase);
    private async Task NoteWithholdingAsync(string targetNodeId, ConsensusMergeSession session)
    {
        int strikes = _withholdStrikes.AddOrUpdate(targetNodeId, 1, (_, n) => n + 1);
        if (strikes < 2) return;
        _logger.LogWarning("[Anti-Tamper Guard] {TargetNodeId} accepted a merge and sent no records ({Strikes} times). Penalizing; evidence kept.", targetNodeId, strikes);
        await _ratingService.ApplyLocalReputationUpdateAsync(targetNodeId, -5);
        if (session.StagingOperation != null)
            ConsensusEvidenceStore.Write(session.StagingOperation.EvidenceDir, new ConsensusEvidence
            {
                Kind = "withholding",
                SubjectNodeId = targetNodeId,
                ConsensusDate = session.StagingOperation.TargetDate,
                DetectedByNodeId = _identity.NodeId,
                Reason = $"Accepted a merge and sent no records, {strikes} times this session (not self-proving: local evidence only).",
                PenalizedNodeIds = new List<string> { targetNodeId },
            }, _logger);
    }

    // ---- measurements for the swarm report: how many nodes this node holds, and the traffic so far
    private int _lastReportedHeld = -1;
    private async Task FinalizeIterationAndReportAsync(ConsensusMergeSession session)
    {
        await _sessionManager.FinalizeIterationStepAsync();
        if (session.StagingOperation == null || !Directory.Exists(session.StagingOperation.MergeDir)) return;   // session over
        int held = HeldNodeIds(session).Count(id => !id.Equals(_identity.NodeId, StringComparison.OrdinalIgnoreCase));
        if (held == _lastReportedHeld) return;
        _lastReportedHeld = held;
        LogConsensusEvent("progress", new Dictionary<string, object?>
        {
            ["held"] = held, ["bytes"] = TrafficCounter.ConsensusBytes, ["msgs"] = TrafficCounter.ConsensusMessages,
        });
    }

    #region Echo rounds (anti-entropy between neighbours)

    private const int EchoIntervalSeconds = 10;

    /// <summary>
    /// What we hold, as id -> version fingerprint (identity hash + signature; trust scores are left out because the
    /// opinion fusion moves them a little on every merge and they would never match), plus one digest over the set.
    /// </summary>
    private async Task<ConsensusEcho> BuildEchoAsync(ConsensusMergeSession session)
    {
        var echo = new ConsensusEcho();
        var op = session.StagingOperation;
        if (op == null) return echo;
        using (await op.LockAsync())
        {
            foreach (var dir in new[] { op.MergeDir, op.IterDir })   // IterDir is newer: it overrides
            {
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
                foreach (var file in Directory.GetFiles(dir, "*.json"))
                {
                    try
                    {
                        var h = ConsensusNodeJson.ReadHistoric(await File.ReadAllTextAsync(file));
                        if (h?.Identity?.NodeId == null) continue;
                        var bytes = h.Identity.GetNodeDataHash().Concat(h.Identity.Signature ?? Array.Empty<byte>()).ToArray();
                        echo.Records[h.Identity.NodeId] = Convert.ToHexString(SHA256.HashData(bytes)).Substring(0, 12);
                    }
                    catch { }
                }
            }
        }
        echo.Digest = XorDigest(echo.Records);
        return echo;
    }

    /// <summary>
    /// Checksum of a record set: XOR of a hash of every "id:version". Order does not matter and it can be kept up to
    /// date one record at a time; two nodes with the same digest hold the same records in the same versions.
    /// (A plain XOR of the ids would miss a record held in another version, and two different ids can cancel each
    /// other out - hashing each entry first avoids both.)
    /// </summary>
    private static string XorDigest(Dictionary<string, string> records)
    {
        var acc = new byte[16];
        foreach (var (id, version) in records)
        {
            var h = SHA256.HashData(Encoding.UTF8.GetBytes(id.ToLowerInvariant() + ":" + version));
            for (int i = 0; i < acc.Length; i++) acc[i] ^= h[i];
        }
        return Convert.ToHexString(acc);
    }

    /// <summary>Ids the other side holds that we don't, or holds in another version.</summary>
    private static List<string> EchoDiff(ConsensusEcho mine, ConsensusEcho theirs) =>
        theirs.Records.Where(kv => !mine.Records.TryGetValue(kv.Key, out var v) || v != kv.Value).Select(kv => kv.Key).ToList();

    /// <summary>
    /// Echo round: every EchoIntervalSeconds, compare digests with each live neighbour. Equal: nothing to do.
    /// Different: each side pulls what it lacks (or holds in another version) from the other, and whatever is
    /// new goes on as a delta - so a difference found anywhere spreads by itself. Quiet rounds (all neighbours
    /// identical) are counted and logged: a connected swarm where every node is quiet has converged.
    /// </summary>
    private async Task EchoRoundAsync(ConsensusMergeSession session, CancellationToken ct)
    {
        if (DateTime.UtcNow - session.LastEchoUtc < TimeSpan.FromSeconds(EchoIntervalSeconds)) return;
        session.LastEchoUtc = DateTime.UtcNow;
        var neighbours = _connectionManager.GetActiveConnections()
            .Where(c => c?.NodeId != null && c.PeerConnection?.connectionState == SIPSorcery.Net.RTCPeerConnectionState.connected)
            .Select(c => c.NodeId).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (neighbours.Count == 0) return;

        var mine = await BuildEchoAsync(session);
        // Step 1 carries only the checksum (a few bytes); lists travel only when the checksums differ
        string payload = JsonSerializer.Serialize(new ConsensusEcho { Digest = mine.Digest });
        string fullPayload = JsonSerializer.Serialize(mine);
        int same = 0, differ = 0, pulled = 0;
        foreach (var neighbour in neighbours)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                var result = await _routerService.SendRequestAsync(neighbour, "ConsensusEcho", payload,
                    new RouteSettings { Timeout = TimeSpan.FromSeconds(10), CancellationToken = ct });
                if (!result.IsSuccess || string.IsNullOrEmpty(result.Payload) || !result.Payload.TrimStart().StartsWith("{")) continue;
                var theirs = JsonSerializer.Deserialize<ConsensusEcho>(result.Payload);
                if (theirs == null) continue;
                if (theirs.Digest == mine.Digest) { same++; continue; }
                differ++;
                var lack = EchoDiff(mine, theirs)
                    .Where(id => !id.Equals(_identity.NodeId, StringComparison.OrdinalIgnoreCase))
                    .Where(id => session.EchoPullsTried.TryAdd($"{neighbour}|{id}|{theirs.Records[id]}", 0))
                    .Take(MaxPullIds).ToList();
                int theyLack = EchoDiff(theirs, mine).Count;
                _logger.LogInformation("[Consensus Protocol] Echo with {Neighbour}: digests differ; we lack {Lack} (pulling), it lacks {TheyLack} (it pulls).",
                    neighbour, lack.Count, theyLack);
                LogConsensusEvent("echo", new Dictionary<string, object?> { ["peer"] = neighbour, ["lack"] = lack.Count, ["they_lack"] = theyLack });
                if (lack.Count > 0) pulled += await PullNodesAsync(neighbour, lack, session, ct);
                if (theyLack > 0)
                    // Step 2: our list, so the neighbour pulls its side of the difference
                    _ = _routerService.SendRequestAsync(neighbour, "ConsensusEcho", fullPayload,
                        new RouteSettings { Timeout = TimeSpan.FromSeconds(10), CancellationToken = ct });
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[Consensus Protocol] Echo with {Neighbour} failed.", neighbour);
            }
        }

        if (pulled > 0)
        {
            await CreateAndBroadcastLocalDeltaAsync(session, ct);   // what the echo brought in goes on
            await FinalizeIterationAndReportAsync(session);
            await SyncMergeDirToConsensusStoreAsync(session);
        }

        if (differ == 0 && same > 0)
        {
            if (++session.QuietEchoRounds == 2)
            {
                _logger.LogInformation("[Consensus Protocol] Echo: all {Count} neighbour(s) hold exactly what we hold ({Records} records), twice in a row.", same, mine.Records.Count);
                LogConsensusEvent("echo_quiet", new Dictionary<string, object?> { ["neighbours"] = same, ["records"] = mine.Records.Count });
            }
        }
        else session.QuietEchoRounds = 0;
    }

    private async Task<string> HandleIncomingEchoAsync(string senderNodeId, P2PPacketEnvelope frame, CancellationToken ct)
    {
        var session = _sessionManager.GetActiveSession();
        if (session == null) return "NO_SESSION_WTF";
        ConsensusEcho? theirs = null;
        try { theirs = JsonSerializer.Deserialize<ConsensusEcho>(frame.SerializedPayload ?? string.Empty); } catch { }
        var mine = await BuildEchoAsync(session);
        if (theirs != null && theirs.Digest == mine.Digest)
            return JsonSerializer.Serialize(new ConsensusEcho { Digest = mine.Digest });   // same: no lists
        if (theirs != null && theirs.Records.Count > 0)
        {
            foreach (var id in theirs.Records.Keys) session.KnownIds.TryAdd(id, 0);
            // Our side of the exchange: pull what the asker has and we don't, then pass it on
            var lack = EchoDiff(mine, theirs)
                .Where(id => !id.Equals(_identity.NodeId, StringComparison.OrdinalIgnoreCase))
                .Where(id => session.EchoPullsTried.TryAdd($"{senderNodeId}|{id}|{theirs.Records[id]}", 0))
                .Take(MaxPullIds).ToList();
            if (lack.Count > 0)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        if (await PullNodesAsync(senderNodeId, lack, session, ct) > 0)
                        {
                            await CreateAndBroadcastLocalDeltaAsync(session, ct);
                            await FinalizeIterationAndReportAsync(session);
                        }
                    }
                    catch (Exception ex) { _logger.LogDebug(ex, "[Consensus Protocol] Echo pull from {Sender} failed.", senderNodeId); }
                });
            }
        }
        return JsonSerializer.Serialize(mine);
    }

    #endregion

    #region Neighbours, pulls and corroboration (no full mesh)

    private bool IsConnected(string nodeId) =>
        _connectionManager.GetConnection(nodeId)?.PeerConnection?.connectionState == SIPSorcery.Net.RTCPeerConnectionState.connected;

    /// <summary>A few nodes to start from when none is connected yet: log2(n) + 1, at least 2.</summary>
    private static int ConsensusFanout(int known) => Math.Max(2, (int)Math.Ceiling(Math.Log2(Math.Max(2, known))) + 1);

    /// <summary>Nodes learned in a merge are visited only when they are our neighbours; the rest reach us through deltas and pulls.</summary>
    private void QueueNeighboursToVisit(IEnumerable<string> nodeIds)
    {
        var all = nodeIds.Where(id => !string.IsNullOrEmpty(id)).ToList();
        var neighbours = all.Where(IsConnected).ToList();
        _sessionManager.QueueNodesToVisit(neighbours);
        if (all.Count > neighbours.Count)
            _logger.LogDebug("[Consensus Protocol] {Skipped} node(s) learned in this merge are not neighbours; not visiting them (deltas/pulls cover them).", all.Count - neighbours.Count);
    }

    /// <summary>Asks one node for the records of the given nodes and stages them with that node as the source.</summary>
    private async Task<int> PullNodesAsync(string fromNodeId, List<string> nodeIds, ConsensusMergeSession session, CancellationToken ct)
    {
        if (session.StagingOperation == null || nodeIds.Count == 0) return 0;
        try
        {
            var result = await _routerService.SendRequestAsync(fromNodeId, "ConsensusNodePull",
                JsonSerializer.Serialize(new ConsensusNodePullRequest { NodeIds = nodeIds.Take(MaxPullIds).ToList() }),
                new RouteSettings { Timeout = TimeSpan.FromSeconds(15), CancellationToken = ct });
            if (!result.IsSuccess || string.IsNullOrEmpty(result.Payload) || !result.Payload.TrimStart().StartsWith("[")) return 0;
            var nodes = JsonSerializer.Deserialize<List<PeerNodeTransferred>>(result.Payload) ?? new List<PeerNodeTransferred>();
            nodes = nodes.Where(n => n?.Identity?.NodeId != null && nodeIds.Contains(n.Identity.NodeId, StringComparer.OrdinalIgnoreCase)).ToList();
            if (nodes.Count == 0)
            {
                _logger.LogInformation("[Consensus Protocol] {From} has none of the {Count} record(s) we asked for ({Ids}).", fromNodeId, nodeIds.Count, string.Join(", ", nodeIds.Take(8)));
                return 0;
            }
            await _mergeEngine.StageIncomingNodeSetAsync(session.StagingOperation, fromNodeId, nodes);
            session.IterationSources.TryAdd(fromNodeId, 0);
            foreach (var n in nodes) await _peerTracker.UpdatePeerWasSeenByAsync(n.Identity.NodeId, fromNodeId);
            _logger.LogInformation("[Consensus Protocol] Pulled {Count} record(s) from {From}: {Nodes}.", nodes.Count, fromNodeId, string.Join(", ", nodes.Select(n => n.Identity.NodeId).Take(8)));
            LogConsensusEvent("pull", new Dictionary<string, object?> { ["peer"] = fromNodeId, ["nodes"] = nodes.Select(n => n.Identity.NodeId).ToList() });
            return nodes.Count;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[Consensus Protocol] Pull from {From} failed.", fromNodeId);
            return 0;
        }
    }

    private async Task<string> HandleIncomingNodePullAsync(string senderNodeId, P2PPacketEnvelope frame, CancellationToken ct)
    {
        var session = _sessionManager.GetActiveSession();
        if (session == null) return "NO_SESSION_WTF";
        ConsensusNodePullRequest? req = null;
        try { req = JsonSerializer.Deserialize<ConsensusNodePullRequest>(frame.SerializedPayload ?? string.Empty); } catch { }
        if (req?.NodeIds == null || req.NodeIds.Count == 0) return "[]";
        var wanted = new HashSet<string>(req.NodeIds.Take(MaxPullIds), StringComparer.OrdinalIgnoreCase);
        foreach (var id in wanted) session.KnownIds.TryAdd(id, 0);   // being asked for a node tells us it exists
        var nodes = _evil.TamperOutgoingNodes(senderNodeId, (await LoadLocalNodesForSwapAsync(session)).Where(n => wanted.Contains(n.Identity.NodeId)).ToList());
        _logger.LogInformation("[Consensus Protocol] {Sender} asked for {Asked} record(s) ({Ids}); we hold {Have}.", senderNodeId, wanted.Count,
            string.Join(", ", wanted.Take(8)), nodes.Count);
        return JsonSerializer.Serialize(nodes);
    }

    /// <summary>
    /// Redundancy without a full mesh: records we got from one source only, and chain signers whose record we
    /// never got, are asked for from up to two neighbours that are not that source. A second source either
    /// confirms the record (it firms up) or contradicts it (a discrepancy, settled and kept as evidence).
    /// </summary>
    private async Task CorroborateAsync(ConsensusMergeSession session, CancellationToken ct)
    {
        var op = session.StagingOperation;
        if (op == null) return;
        var held = HeldNodeIds(session);
        foreach (var p in _peerTracker.GetLastSeenNodes(PeerTracker.DefaultNodeComparer))
            if (p?.Identity?.NodeId != null && p.Identity.Roles.Contains("tracker")) session.KnownIds.TryAdd(p.Identity.NodeId, 0);
        var single = op.Observations.SingleSource().Where(x => !x.NodeId.Equals(_identity.NodeId, StringComparison.OrdinalIgnoreCase)).ToList();
        var missing = session.UnconfirmedSigners.Keys.Concat(session.KnownIds.Keys).Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(id => !held.Contains(id) && !id.Equals(_identity.NodeId, StringComparison.OrdinalIgnoreCase)).ToList();
        if (single.Count == 0 && missing.Count == 0) return;

        // Single-source records: asked once per distinct set. Missing records are normally pushed to us by deltas
        // (whoever learns something new passes it on); asking is only the fallback, every 30 s, for a record that
        // nobody near us happens to push.
        string setKey = string.Join(",", single.Select(x => x.NodeId).OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
        bool askSingle = setKey != session.LastCorroborationKey;
        bool askMissing = missing.Count > 0 && DateTime.UtcNow - session.LastMissingPullUtc > TimeSpan.FromSeconds(30);
        if (!askSingle && !askMissing) return;
        session.LastCorroborationKey = setKey;
        if (askMissing) session.LastMissingPullUtc = DateTime.UtcNow;
        if (!askSingle) single = new List<(string NodeId, string Source)>();
        if (!askMissing) missing = new List<string>();
        if (missing.Count > 0)
            _logger.LogInformation("[Consensus Protocol] Holding {Held} of {Known} known node(s); missing: {Missing} (round {Round}).",
                held.Count, session.KnownIds.Count, string.Join(", ", missing.Take(12)), session.MissingPullRounds + 1);

        var neighbours = _connectionManager.GetActiveConnections()
            .Where(c => c?.NodeId != null && c.PeerConnection?.connectionState == SIPSorcery.Net.RTCPeerConnectionState.connected)
            .Select(c => c.NodeId).OrderBy(_ => Random.Shared.Next()).ToList();
        if (neighbours.Count == 0) return;

        int asked = 0, got = 0;
        foreach (var neighbour in neighbours)
        {
            if (asked >= 2 || ct.IsCancellationRequested) break;
            var ids = single.Where(x => !x.Source.Equals(neighbour, StringComparison.OrdinalIgnoreCase) && !x.NodeId.Equals(neighbour, StringComparison.OrdinalIgnoreCase))
                .Select(x => x.NodeId).Concat(missing).Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxPullIds).ToList();
            if (ids.Count == 0) continue;
            asked++;
            got += await PullNodesAsync(neighbour, ids, session, ct);
        }
        if (asked > 0)
            _logger.LogInformation("[Consensus Protocol] Asked {Asked} neighbour(s) to confirm {Single} single-source and {Missing} missing record(s); {Got} record(s) came back.",
                asked, single.Count, missing.Count, got);

        // Still missing after two rounds of asking neighbours: the node itself holds its own record - ask it directly
        if (missing.Count > 0)
        {
            var stillMissing = missing.Where(id => !HeldNodeIds(session).Contains(id)).ToList();
            if (stillMissing.Count == 0) session.MissingPullRounds = 0;
            else if (++session.MissingPullRounds >= 2)
            {
                foreach (var id in stillMissing.Take(3))
                {
                    if (ct.IsCancellationRequested) break;
                    _logger.LogInformation("[Consensus Protocol] No neighbour has {NodeId}'s record; asking {NodeId} directly.", id, id);
                    if (await EnsurePeerConnectionAsync(id))
                        got += await PullNodesAsync(id, new List<string> { id }, session, ct);
                }
            }
        }
        if (got > 0)
        {
            await CreateAndBroadcastLocalDeltaAsync(session, ct);   // only differences go out (missing ones found)
            await FinalizeIterationAndReportAsync(session);
            await SyncMergeDirToConsensusStoreAsync(session);
        }
    }

    #endregion

    private async Task<List<PeerNodeTransferred>> LoadLocalNodesForSwapAsync(ConsensusMergeSession session)
    {
        var list = new List<PeerNodeTransferred>();
        if (session.StagingOperation == null) return list;

        string mergeDir = session.StagingOperation.MergeDir;
        using var gate = await session.StagingOperation.LockAsync();
        if (!Directory.Exists(mergeDir)) return list;

        foreach (var file in Directory.GetFiles(mergeDir, "*.json"))
        {
            try
            {
                string json = await File.ReadAllTextAsync(file);
                PeerNodeHistoric? historic = null;
                historic = ConsensusNodeJson.ReadHistoric(json);

                if (historic?.Identity != null)
                {
                    list.Add(new PeerNodeTransferred
                    {
                        Identity = historic.Identity,
                        Signatures = historic.Signatures ?? new List<SignatureEnvelope>(),
                        SignatureScore = historic.SignatureScore,
                        TrustScore = historic.TrustScore,
                        LastSeen = historic.LastSeen
                    });
                }
            }
            catch { }
        }

        return list;
    }

    private void RegisterConsensusNetworkHandlers()
    {
        _routerService.RegisterCommandHandler("ConsensusMergeInitRequest", HandleIncomingMergeInitRequestAsync);
        _routerService.RegisterCommandHandler("ConsensusSwap", HandleIncomingNodeSwapAsync);
        _routerService.RegisterCommandHandler("ConsensusSignatureRequest", HandleIncomingSignatureRequestAsync);
        _routerService.RegisterCommandHandler("ConsensusDeltaPing", HandleIncomingDeltaPingAsync);
        _routerService.RegisterCommandHandler("ConsensusDeltaDownload", HandleIncomingDeltaDownloadAsync);
        _routerService.RegisterCommandHandler("ConsensusFinished", HandleIncomingConsensusFinishedAsync);
        _routerService.RegisterCommandHandler("ConsensusNodePull", HandleIncomingNodePullAsync);
        _routerService.RegisterCommandHandler("ConsensusEcho", HandleIncomingEchoAsync);
    }

    private void UnregisterConsensusNetworkHandlers()
    {
        _routerService.UnregisterCommandHandler("ConsensusMergeInitRequest");
        _routerService.UnregisterCommandHandler("ConsensusSwap");
        _routerService.UnregisterCommandHandler("ConsensusSignatureRequest");
        _routerService.UnregisterCommandHandler("ConsensusDeltaPing");
        _routerService.UnregisterCommandHandler("ConsensusDeltaDownload");
        _routerService.UnregisterCommandHandler("ConsensusFinished");
        _routerService.UnregisterCommandHandler("ConsensusNodePull");
        _routerService.UnregisterCommandHandler("ConsensusEcho");
    }

    /// <summary>Appends a line to consensus_events.jsonl (see <see cref="ConsensusEventLog"/>).</summary>
    private void LogConsensusEvent(string ev, IDictionary<string, object?>? extra = null)
    {
        ConsensusEventLog.Write(_p2pSettings.CurrentValue.DataStoragePath, _identity.NodeId, ev,
            _sessionManager.GetActiveSession()?.SessionId, extra);
    }

    private int KnownPeerCount()
    {
        try
        {
            return _peerTracker.GetLastSeenNodes(PeerTracker.DefaultNodeComparer)
                .Count(p => p.Identity?.NodeId != null && !p.Identity.NodeId.Equals(_identity.NodeId, StringComparison.OrdinalIgnoreCase));
        }
        catch { return -1; }
    }

    private async Task CompleteAndFinalizeSessionAsync(string reason = "done")
    {
        // Average size of one node record as it travels (for the overhead coefficient in the report)
        int recBytes = 0;
        try
        {
            var s = _sessionManager.GetActiveSession();
            if (s != null)
            {
                var recs = await LoadLocalNodesForSwapAsync(s);
                if (recs.Count > 0) recBytes = (int)recs.Average(r => JsonSerializer.Serialize(r).Length);
            }
        }
        catch { }
        LogConsensusEvent("finish", new Dictionary<string, object?>
        {
            ["reason"] = reason, ["known"] = KnownPeerCount(),
            ["bytes"] = TrafficCounter.ConsensusBytes, ["msgs"] = TrafficCounter.ConsensusMessages, ["rec_bytes"] = recBytes,
        });
        UnregisterConsensusNetworkHandlers();
        await _sessionManager.FinalizeSessionAsync();

        // Self-promotion (if it happened at all this round) is strictly for driving THIS round
        // when nothing else was around to do it - always demote once the round is done,
        // regardless of whether a real tracker ever showed up. Several isolated peers can end up
        // briefly self-promoted at once; that's fine, each demotes after its own round and that
        // demotion gossips out like any other role change - no coordination needed between them.
        _ = EnsureSelfDemotedAsync();
    }

    private byte[] ComputeChainLinkSignature(byte[]? previousSig, int order, string signerNodeId, string targetNodeId, long timestampTicks, byte[] daySignatureKey)
    {
        if (daySignatureKey == null || daySignatureKey.Length == 0) return Array.Empty<byte>();

        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        if (previousSig != null && previousSig.Length > 0)
        {
            writer.Write(previousSig.Length);
            writer.Write(previousSig);
        }

        writer.Write(order);
        writer.Write(signerNodeId ?? string.Empty);
        writer.Write(targetNodeId ?? string.Empty);
        writer.Write(timestampTicks);
        writer.Flush();

        byte[] payloadHash = SHA256.HashData(ms.ToArray());
        return P2PCrypto.SignData(payloadHash, daySignatureKey);
    }

    private bool IsMidnightBoundaryReached()
    {
        var now = DateTime.UtcNow;
        return now.Hour == 0 && now.Minute < 5;
    }

    public void Dispose()
    {
        _sessionLoopCts?.Cancel();
        _sessionLoopCts?.Dispose();
        UnregisterConsensusNetworkHandlers();
        _routerService.UnregisterCommandHandler("ConsensusStartPing");
    }
}