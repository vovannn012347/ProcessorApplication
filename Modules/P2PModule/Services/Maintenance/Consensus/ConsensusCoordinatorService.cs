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

namespace P2PModule.Services.Maintenance.Consensus;

/*
#region Associated DTO and State Classes

public class ConsensusDeltaPingPending
{
    public HashSet<string> TargetNodes { get; set; } = new();

    public ConsensusDeltaPing Delta { get; set; }
}

public enum ConsensusSessionType
{
    Peer,
    Tracker
} 

/// <summary>
/// Context manager class containing all session details for an active daily consensus run.
/// Governs global set convergence parameters, locks, and task scheduling queues.
/// </summary>
public class ConsensusMergeSession
{
    public ConsensusSessionType Type { get; set; } = ConsensusSessionType.Tracker;
    public string DayKey { get; set; } = DateTime.UtcNow.ToString("yyyy-MM-dd");
    public DateTime DayDateKey { get; set; } = DateTime.UtcNow;

    public byte[] DayKeyPrivateKey { get; set; } = Array.Empty<byte>();
    public byte[] DayKeyPublicKey { get; set; } = Array.Empty<byte>();
    public byte[] DayKeySignatureKey { get; set; } = Array.Empty<byte>();
    public byte[] DayKeyVerifyKey { get; set; } = Array.Empty<byte>();

    public string BaseMergeDir { get; set; } = string.Empty;

    /// <summary>
    /// generated outgoing deltas by GenerateIterationDelta
    /// </summary>
    public string MergeDeltaDir { get; set; } = string.Empty;
    public string CurrentTargetNodeId { get; set; } = string.Empty;

    //our chain signatures
    public string ChainSignatureLastNode { get; set; } = string.Empty;
    public byte[] ChainSignaturesLastNodeVerify { get; set; } = new byte[0];

    //// Sets keeping track of disjoint cluster sets to manage backpropagation wave boundaries
    public HashSet<string> CurrentSyncSet { get; set; } = new();
    public HashSet<string> PreviousSyncSet { get; set; } = new();

    // Queue of nodes yet to visit during this consensus iteration
    public Queue<string> NodesToVisit { get; set; } = new();
    public Queue<string> BusyNodes { get; set; } = new();
    public HashSet<string> NodesVisited { get; set; } = new();
    /// <summary>
    /// consensus start request
    /// </summary>
    public ConsensusRequest? CurrentCSR { get; set; } = null;
    /// <summary>
    /// personal consensus start request
    /// </summary>
    public ConsensusRequest? MyCSR { get; set; } = null;
    public ConsensusMergeOperation Operation { get; set; } = new();
    public SessionOperationControl Control { get; set; } = new();
    public DeltasInSession Deltas { get; set; } = new();

    public class DeltasInSession
    {
        public readonly object DeltaLock = new();
        public string IncomingDeltasDir { get; set; } = string.Empty; // ConsensusIncomingDeltas (received pull-only deltas)

        public List<ChainSignature> IncomingChainSignatures { get; set; } = new();

        public List<ChainSignature> OurChainSignatures { get; set; } = new();

        /// <summary>
        /// </summary>

        /// <summary>
        /// key: node id for pending delta; 
        /// value: identifier of unsigned delta
        /// list of unsigned deltas we made and we need to sign
        /// </summary>
        public ConcurrentDictionary<string, ConsensusDeltaPingPending> PendingDeltas { get; set; } = new();

        /// <summary>
        /// key: node id of the chain
        /// signature chains from nodes we visited
        /// when we get a new signature chain - we verify it with our one
        /// </summary>
        public ConcurrentDictionary<string, List<ChainSignature>> DeltaChainSignatures { get; set; } = new();
        
        /// <summary>
        /// list of incoming deltas we have due to sync
        /// point straight to delta folder on disk
        /// </summary>
        public List<string> DeltasToSync { get; set; } = new();

        /// <summary>
        /// list of previous deltas made by us
        /// key: delta identifier
        /// value: expiration
        /// </summary>
        public Dictionary<string, DateTime> AvailableDeltas { get; set; } = new();
        /// <summary>
        /// key: nodeid from which is delta
        /// value: signature
        /// </summary>
        public Dictionary<string, byte[]> DeltaSignatures { get; set; } = new();
        //
        public Dictionary<string, byte[]> DeltaNewVerifyKeySignatures { get; set; } = new();
        public Dictionary<string, CancellationTokenSource> DeltaTasks { get; set; } = new();
        /// <summary>
        /// id: sendernodeid
        /// value: delta task
        /// </summary>
        public ConcurrentDictionary<string, Task> ActiveDeltaDownloads { get; set; } = new();
    }

    public class SessionOperationControl {
        /// <summary>
        /// Concurrent synchronization flag limiting each node to exactly 1 active merge connection at a time
        /// </summary>
        public int ProcessingLock = 0;

        public CancellationTokenSource? OrchestrationCts { get; set; }
        public CancellationTokenSource? NodeMergeCt { get; set; }
    }
}


/// <summary>
/// Coordinator class specifically managing uncommitted disk staging directory merges,
/// rollback iterations, and uncommitted staged files for the *current* peer-to-peer transaction.
/// </summary>
//public class ConsensusMergeOperation
//{
//    public string TargetDate { get; set; } = string.Empty;
//    public string MergeDir { get; set; } = string.Empty;        // ConsensusCurrentMerge (finalized iteration results)
//    public string IterDir { get; set; } = string.Empty;         // ConsensusCurrentIter (temporary uncommitted staging)
//    public string BasePath { get; set; }
//}

#endregion


/// <summary>
/// Implements WebRTC-based consensus query handlers, progressive/segmented list chunking,
/// and senior-weighted conflict resolution using pure mathematical anti-Sybil rules.
/// </summary>
public class ConsensusCoordinatorService : BackgroundService, IConsensusCoordinatorService, IDisposable
{
    public const string OutboundConnectionType = "outbound";

    private readonly ILogger<ConsensusCoordinatorService> _logger;
    private readonly IOptionsMonitor<P2PSettings> _p2pSettings;
    private readonly IOptionsMonitor<RatingSettings> _ratingSettings;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IP2PRouterService _routerService;
    private readonly IPeerConnectionManager _conenctionService;
    private readonly IPeerTracker _peerTracker;
    private readonly IConsensusStore _consensusStore;
    private readonly INodeIdentityService _identity;
    private readonly IRatingService _ratingService;

    // Decoupled active session and iteration tracking
    private string _currentEpochDayKey = string.Empty;
    private ConsensusMergeSession? _activeSession;
    private Dictionary<string, ConsensusRequest> _consensusStartRequests = new();
    private readonly object _epochLock = new();

    private bool _receivedStartSignal = false;
    private bool _receivedDeltaGossip = false;
    private bool _receivedEndSignal = false;
    private CancellationTokenSource? _endSessionCts;

    private ConsensusMergeOperation? _currentMergeIteration;

    //public event Action? NodeAdvertiseRequired;
    public event Action? ConsensusStarted;
    public event Action? ConsensusEnded;

    public ConsensusCoordinatorService(
        ILogger<ConsensusCoordinatorService> logger,
        IOptionsMonitor<P2PSettings> settings,
        IOptionsMonitor<RatingSettings> ratingSettings,
        IServiceScopeFactory scopeFactory,
        IP2PRouterService routerService,
        IPeerConnectionManager conenctionService,
        INodeIdentityService identity,
        IPeerTracker peerTracker,
        IConsensusStore consensusStore,
        IRatingService ratingService)
    {
        _scopeFactory = scopeFactory;
        _routerService = routerService;
        _conenctionService = conenctionService;
        _peerTracker = peerTracker;
        _consensusStore = consensusStore;
        _identity = identity;
        _ratingService = ratingService;
        _p2pSettings = settings;
        _ratingSettings = ratingSettings;
        _logger = logger;
    }


    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[Consensus Coordinator] Background time monitor initialized.");
        RegisterNetworkCommandHandlers();

        while (!stoppingToken.IsCancellationRequested)
        {
            var utcNow = DateTime.UtcNow;
            string todayKey = utcNow.ToString("yyyy-MM-dd");

            // Terminate or skip instantly if we had consensus completed recently for today
            // or we are in process of of one
            if (_activeSession != null || _currentMergeIteration != null || _currentEpochDayKey == todayKey)
            {
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
                continue;
            }

            // Check if we are inside the daily 1-hour merge window (23:00 - 23:59 UTC)
            if (utcNow.Hour == 23)
            {
                var roles = _identity.GetCapacities();
                bool isLocalNodeTracker = roles.Contains("tracker") || roles.Contains("promoted_tracker");

                // Dynamic activity metric: Only "tracker" prevents peer loop, "promoted_tracker" is excluded intentionally
                var connectedTrackers = _peerTracker.GetLastSeenNodes(PeerTracker.DefaultNodeComparer)
                    .Where(c => c.Identity.Roles.Contains("tracker") && c.LastSeen.AddMinutes(5) > DateTime.UtcNow)
                    .ToList();

                bool hasActiveTrackerConnection = connectedTrackers.Any();

                // 2. Silent Tracker Grace Timeout Check (10 Minutes passed with no start/delta signal)
                bool trackerSilentTimeout = utcNow.Minute >= 10 && !_receivedStartSignal && !_receivedDeltaGossip;

                // 3. Enforce Role Sovereignty Gate (Only Standdown passively if tracker connection is active and NOT timed out)
                if (!isLocalNodeTracker && hasActiveTrackerConnection && !trackerSilentTimeout)
                {
                    _logger.LogDebug("[Consensus Coordinator] Role Sovereign Standdown: Connected to tracker. Waiting for start signal passively...");
                    await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
                    continue;
                }

                // If our connected tracker went silent, penalize it immediately to update its rating score
                if (!isLocalNodeTracker && hasActiveTrackerConnection && trackerSilentTimeout)
                {
                    _logger.LogWarning("[Consensus Coordinator] Connected trackers went silent (grace period expired). Dropping trust and triggering fallback merge loop.");
                    foreach (var tracker in connectedTrackers)
                    {
                        if (!string.IsNullOrEmpty(tracker.Identity.NodeId))
                        {
                            await _peerTracker.UpdateTrustAsync(tracker.Identity.NodeId, -15);
                        }
                    }
                }

                // 4. Trigger active orchestrations (As tracker, promoted_tracker, or fallback peer loop)
                try
                {
                    if (!isLocalNodeTracker)
                    {
                        _logger.LogWarning("[Consensus Coordinator] Orphan or Unserved Peer. Triggering fallback Tree Orchestration Loop.");
                    }

                    _logger.LogWarning("[Consensus Coordinator] UTC midnight approaches. Starting consensus merge for date key: {Day}", todayKey);

                    await RunTreeOrchestrationLoopAsync(utcNow, stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[Consensus Coordinator] Error encountered inside main time monitoring loop.");
                }

                Cleanup();
            }

            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
    }


    private bool IsAlphanumeric(string str)
    {
        if (string.IsNullOrEmpty(str)) return false;
        return str.All(c => char.IsLetterOrDigit(c) || c == '_' || c == '-');
    }
    public static string GenerateDeltaIdentifierName(int length = 20)
    {
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";

        char[] result = new char[length];
        byte[] randomBytes = new byte[length];

        RandomNumberGenerator.Fill(randomBytes);

        for (int i = 0; i < length; i++)
        {
            result[i] = chars[randomBytes[i] % chars.Length];
        }

        return new string(result);
    }


    #region Coordinator Actions
    private void RegisterNetworkCommandHandlers()
    {
        _logger.LogInformation("[Consensus Coordinator] Registering RPC communication command listeners.");

        // Handle progressiveSegment requests (Direct Consensus Read on boot)
        _routerService.RegisterCommandHandler("RequestConsensusLatestSegmented", RequestConsensusLatestSegmented_Handler);

        //tracker part
        _routerService.RegisterCommandHandler("ConsensusMergeInitRequest", TrackerMergeRequest_Handler);
        //peer part
        _routerService.RegisterCommandHandler("ConsensusStartSignal", PeerConsensusStartMessage_Handler);
    }

    private void UnregisterNegotiationNetworkCommandHandlers()
    {
        _routerService.UnregisterCommandHandler("ConsensusMergeRequest");
        _routerService.UnregisterCommandHandler("ConsensusSignature");
        _routerService.UnregisterCommandHandler("ConsensusDeltaSignalling");
        _routerService.UnregisterCommandHandler("RequestConsensusDelta");
    }

    private void RegisterNegotiationNetworkCommandHandlers()
    {
        _routerService.RegisterCommandHandler("ConsensusMergeRequest", ConsensusMergeRequest_Handler);
        _routerService.RegisterCommandHandler("ConsensusSignature", HandleConsensusSignatureAsync_Handler);

        //progressive message
        _routerService.RegisterCommandHandler("ConsensusDeltaSignalling", HandleIncomingDeltaSignallingAsync_Handler);
        //progressive request
        _routerService.RegisterCommandHandler("RequestConsensusDelta", HandleRequestConsensusDelta_Handler);
    }

    private void RegisterPeerNetworkCommandHandlers()
    {
        _routerService.RegisterCommandHandler("ConsensusEndSignal", HandleConsensusEndSignal_Handler);
    }
    private void UnregisterPeerNetworkCommandHandlers()
    {
        _routerService.UnregisterCommandHandler("ConsensusEndSignal");
    }

    private async Task BroadcastStartConsensusSignalAsync(ConsensusRequest req)
    {
        _logger.LogInformation("[Consensus] Firing ConsensusStartSignal out to peers.");
        await _routerService.FanoutBroadcastMessage("ConsensusStartSignal", JsonSerializer.Serialize(req));
    }

    private async Task BroadcastEndConsensusSignalAsync()
    {
        _logger.LogInformation("[Consensus] Firing ConsensusEndSignal out to peers.");
        await _routerService.FanoutBroadcastMessage("ConsensusEndSignal", "{}");
    }

    /// <summary>
    /// send message to peers so that they can download new consensus already
    /// </summary>
    private async Task<string> HandleConsensusEndSignal_Handler(string senderNodeId, P2PPacketEnvelope envelope, CancellationToken token)
    {
        _logger.LogInformation("[Consensus Signaling] Received Consensus End Signal from Tracker: {Sender}. Merging and updating daily map.", senderNodeId);
        _receivedEndSignal = true;
        _receivedDeltaGossip = true; // reset watchdog timeout
        ConsensusEnded?.Invoke();

        // Symmetrical Dilation: download the finalized consensus directory with a random concise time dilation
        _ = Task.Run(async () =>
        {
            var rng = new Random();
            int dilationMs = rng.Next(10000, 45000); // 10 to 45 seconds to prevent DDoS spike on Trackers
            _logger.LogInformation("[Consensus Dilation] Postponing consensus snapshot fetch by {Ms}ms to distribute traffic load.", dilationMs);

            await Task.Delay(dilationMs, token);

            if (_activeSession == null)
            {
                _logger.LogWarning("[Consensus Dilation] No active session to download consensus into. Abandoning pull.");
                return;
            }

            int configuredTimeout = _p2pSettings.CurrentValue.StartupDelaySeconds > 0 ? _p2pSettings.CurrentValue.StartupDelaySeconds : 10;
            var interChunkTimeout = TimeSpan.FromSeconds(configuredTimeout / 2 > 0 ? configuredTimeout / 2 : 5);

            _activeSession.CurrentTargetNodeId = senderNodeId;
            bool trackerSuccess = false;

            // Attempt Strategy A: Progressive merge request
            var progressiveTcs = new TaskCompletionSource<bool>();

            try
            {
                await _routerService.SendRequestProgressiveAsync(
                    senderNodeId,
                    "ConsensusMergeRequest",
                    "{}",
                    async (chunkJson, error, progress, finished) => ConsensusMergeResponseIncoming_Handler(progressiveTcs, chunkJson, error, progress, finished),
                    new RouteSettings { Timeout = interChunkTimeout, CancellationToken = token });

                trackerSuccess = await progressiveTcs.Task;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("[Consensus Dilation] Strategy A failed with Tracker {Id}: {Msg}. Attempting fallback.", senderNodeId, ex.Message);
                trackerSuccess = false;
            }

            // Attempt Strategy B Fallback: Finalized segmented consensus request
            if (!trackerSuccess)
            {
                _logger.LogWarning("[Consensus Dilation] Strategy A failed or returned error. Falling back to Strategy B (RequestConsensusLatestSegmented) from tracker {Id}...", senderNodeId);

                var coldTcs = new TaskCompletionSource<bool>();

                try
                {
                    await _routerService.SendRequestProgressiveAsync(
                        senderNodeId,
                        "RequestConsensusLatestSegmented",
                        JsonSerializer.Serialize(new { BatchSize = 40 }),
                        async (chunkJson, error, progress, finished) =>
                        {
                            if (_activeSession == null)
                            {
                                _logger.LogInformation("[Consensus Merge] Consensus merge on empty session. Skipping.");
                                coldTcs.TrySetResult(false);
                                return;
                            }

                            if (!string.IsNullOrEmpty(error) || string.IsNullOrEmpty(chunkJson) || chunkJson.StartsWith("NOPE") || chunkJson.StartsWith("NO_ITERATION_ACTIVE"))
                            {
                                if (!string.IsNullOrEmpty(error))
                                {
                                    _logger.LogInformation("[Consensus Merge] Error on data segment from Tracker {Id}: {error}", _activeSession.CurrentTargetNodeId, error);
                                }
                                coldTcs.TrySetResult(false);
                                return;
                            }

                            if (!string.IsNullOrEmpty(chunkJson))
                            {
                                var docSegment = JsonSerializer.Deserialize<NodesTransferContainer>(chunkJson);
                                if (docSegment != null && docSegment.Nodes != null)
                                {
                                    StageIncomingNodesAsync(docSegment.Nodes, senderNodeId);
                                }
                            }

                            if (finished) coldTcs.TrySetResult(true);
                        },
                        new RouteSettings
                        {
                            Timeout = interChunkTimeout,
                            CancellationToken = token
                        });

                    trackerSuccess = await coldTcs.Task;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[Consensus Dilation] Strategy B failed with Tracker {Id}.", senderNodeId);
                    trackerSuccess = false;
                }
            }

            if (trackerSuccess)
            {
                _logger.LogInformation("[Consensus Dilation] Successfully accumulated consensus state from End Node: {Id}", senderNodeId);
            }
        }, token);

        // In Peer mode, defer the session end to 10 minutes after the last consensus end signal is received
        if (_activeSession != null && _activeSession.Type == ConsensusSessionType.Peer)
        {
            _endSessionCts?.Cancel();
            _endSessionCts = new CancellationTokenSource();
            var localCts = _endSessionCts;

            _logger.LogInformation("[Consensus Signaling] Peer mode: Deferring final session closure to exactly 10 minutes after last end signal.");

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromMinutes(10), localCts.Token);
                    if (!localCts.IsCancellationRequested)
                    {
                        _logger.LogInformation("[Consensus Signaling] 10 minutes elapsed since the last ConsensusEndSignal. Committing and terminating peer merge session.");

                        // Commit the final consolidated merge output securely before cleaning up!
                        await CommitTreeMergeAsync();
                        
                        Cleanup();
                    }
                }
                catch (TaskCanceledException) { }
            });
        }
        return "SIGNAL_ACK";
    }

    private async Task<string> RequestConsensusLatestSegmented_Handler(string senderNodeId, P2PPacketEnvelope envelope, CancellationToken cancelToken)
    {
        _logger.LogInformation("[Consensus Coordinator] Serving latest finalized consensus segments to peer: {Id}", senderNodeId);
        // Fire-and-forget: Start the task immediately on the thread pool

        // 1. Get the most recent successfully converged dayKey (descending order)
        var latestDayKey = _consensusStore.GetAvailableHistoricalDays()
            .OrderByDescending(d => d)
            .FirstOrDefault();

        if (string.IsNullOrEmpty(latestDayKey))
        {
            await _routerService.SendProgressiveResponseAsync(senderNodeId, envelope.MessageId, envelope.CommandType, "{}", 100, true, "NO_DATA");
            return string.Empty;
        }

        // 2. Retrieve the list of Node IDs for the latest day
        var nodeIds = _consensusStore.GetHistoricalPeers(latestDayKey);
        if (nodeIds == null || nodeIds.Count == 0)
        {
            await _routerService.SendProgressiveResponseAsync(senderNodeId, envelope.MessageId, envelope.CommandType, "{}", 100, true, "NO_DATA");
            return string.Empty;
        }

        int totalNodesCount = nodeIds.Count;
        int batchSize = 40;
        int totalChunks = (int)Math.Ceiling((double)totalNodesCount / batchSize);

        // 3. Progressive chunking of finalized consensus data
        for (int i = 0; i < totalChunks; i++)
        {
            var chunkIds = nodeIds.Skip(i * batchSize).Take(batchSize).ToList();
            var nodesInChunk = new List<PeerNodeTransferred>();

            foreach (var id in chunkIds)
            {
                var peerHistoric = _consensusStore.GetHistoricalPeer(latestDayKey, id);
                if (peerHistoric != null)
                {
                    nodesInChunk.Add(new PeerNodeTransferred
                    {
                        Identity = peerHistoric.Identity,
                        TrustScore = peerHistoric.TrustScore,
                        LastSeen = peerHistoric.LastSeen
                    });
                }
            }

            var docSegment = new NodesTransferContainer
            {
                Nodes = nodesInChunk
            };

            string chunkSerialized = JsonSerializer.Serialize(docSegment);
            double progressPercent = (double)(i + 1) / totalChunks * 100.0;
            bool isCompleted = (i == totalChunks - 1);

            await _routerService.SendProgressiveResponseAsync(
                senderNodeId,
                envelope.MessageId,
                envelope.CommandType,
                chunkSerialized,
                progressPercent,
                isCompleted);

            await Task.Delay(30, cancelToken);
        }

        return string.Empty;
    }

    /// <summary>
    /// Handles ConsensusMergeInitRequest incoming requests from remote trackers or peers.
    /// Manages role redirection, clemency grace passes, and out-of-time subjective logic ratings checks.
    /// </summary>
    private async Task<string> TrackerMergeRequest_Handler(string senderNodeId, P2PPacketEnvelope envelope, CancellationToken cancelToken)
    {
        // Record who asked and test the signature
        var request = JsonSerializer.Deserialize<ConsensusRequest>(envelope.SerializedPayload);
        if (request == null)
        {
            return "FUCK_DO_YOU_SEND";
        }

        var utcNow = DateTime.UtcNow;
        bool isWithinOfficialMergeWindow = utcNow.Hour == 23;

        // Resolve cached profile definitions from local persistence shards
        var remoteNode = _peerTracker.GetPeer(senderNodeId);
        bool isSenderTracker = remoteNode != null && (remoteNode.Identity.Roles.Contains("tracker") || remoteNode.Identity.Roles.Contains("promoted_tracker"));
        double senderTrustScore = remoteNode?.TrustScore ?? 0;

        var localRoles = _identity.GetCapacities();
        bool isLocalNodeTracker = localRoles.Contains("tracker") || localRoles.Contains("promoted_tracker");

        var lastSeenNodes = _peerTracker.GetLastSeenNodes(PeerTracker.DefaultNodeComparer).ToList();
        bool hasActiveTrackerConnection = lastSeenNodes
                .Any(c => c.Identity.Roles.Contains("tracker") && c.LastSeen.AddMinutes(5) > DateTime.UtcNow);

        // =========================================================================
        // BOUNDARY SECURITY GATE 1: Temporal & Role Anomaly Enforcement
        // =========================================================================
        if (!isWithinOfficialMergeWindow)
        {
            // Out-of-time requests are completely illegal unless originating from an authoritative tracker
            if (!isSenderTracker)
            {
                _logger.LogWarning("[Consensus Border] Rejected out-of-time request from Client Node: {NodeId}", senderNodeId);
                return "REJECT_OUT_OF_TIME_NODE_UNAUTHORIZED";
            }

            // Client peers drop out-of-time peer sync requests if they already have an active tracker channel handling updates
            if (!isLocalNodeTracker && hasActiveTrackerConnection)
            {
                _logger.LogWarning("[Consensus Border] Rejected peer interaction: Tracker is already active.");
                return "REJECT_ROLE_BYPASS_ATTEMPT";
            }
        }

        // =========================================================================
        // BOUNDARY SECURITY GATE 2: Clemency & Post-Grace Routing Exceptions
        // =========================================================================
        // Peer Redirect Grace Pass (5-Minute Window starting at 23:05 UTC)
        if (isWithinOfficialMergeWindow && utcNow.Minute < 5 && !isLocalNodeTracker && hasActiveTrackerConnection)
        {
            // First 5 minutes of official window: Client peers are protected from out-of-sync penalties.
            // Rather than starting a redundant loop, we gracefully return our active working tracker details
            var activeTrackerNode = lastSeenNodes
                .FirstOrDefault(c => c.Identity.Roles.Contains("tracker") && c.LastSeen.AddMinutes(5) > DateTime.UtcNow);
            string trackerAddress = activeTrackerNode != null ? activeTrackerNode.Identity.SignalingEndpoints?.FirstOrDefault() ?? "no-active-tracker-cached" : "no-active-tracker-cached";

            _logger.LogInformation("[Consensus Border] Grace period redirect. Returning working tracker endpoint to node: {Id}", senderNodeId);
            return $"REDIRECT_TO_TRACKER|{trackerAddress}";
        }

        if (_activeSession == null || // start a new session request
              !(_consensusStartRequests.TryGetValue(senderNodeId, out var _startingRequest) &&
                  _startingRequest.Signature != request.Signature))
        {
            // Check time bounds
            if (request.TimeStamp.Date == DateTime.UtcNow.Date &&
                request.TimeStamp < DateTime.UtcNow.AddMinutes(5))
            {
                // Verify sender cryptographic records
                var node = GetWeightedIterationNode(request.NodeIdFrom);
                if (node != null && node.NodeData.LastSeen.Date == request.TimeStamp.Date)
                {
                    if (P2PCrypto.TestSignature(request.GetHash(), node.NodeData.Identity.PublicVerifyKey, request.Signature))
                    {
                        if (_consensusStartRequests.TryGetValue(senderNodeId, out var _checkingRequest))
                        {
                            if (request.NonTransitive && _checkingRequest.TimeStamp < request.TimeStamp)
                            {
                                _consensusStartRequests[senderNodeId] = request;
                            }
                        }
                        else
                        {
                            _consensusStartRequests[senderNodeId] = request;
                        }
                    }
                    else
                    {
                        return "FUCK_THIS_REQUEST";
                    }
                }
                else
                {
                    if (_consensusStartRequests.TryGetValue(senderNodeId, out var _checkingRequest))
                    {
                        if (request.NonTransitive && _checkingRequest.TimeStamp < request.TimeStamp)
                        {
                            _consensusStartRequests[senderNodeId] = request;
                        }
                    }
                    else
                    {
                        _consensusStartRequests[senderNodeId] = request;
                    }

                    return "FUCK_YOU_ARE";
                }
            }
            else
            {
                return "FUCK_TIMESTAMP";
            }
        }

        // =========================================================================
        // DECISION TREE: Peer vs Tracker Mode Handling
        // =========================================================================
        bool startSession = false;
        lock (_epochLock)
        {
            startSession = _activeSession == null;
        }

        if (startSession)
        {
            // Peer and Tracker Role-Sovereignty Routing logic
            if (!isLocalNodeTracker)
            {
                // We are a PEER receiving an incoming merge request
                if (isSenderTracker)
                {
                    // "if request is from a tracker - act like a tracker - provide known nodes and sign stuffs but do not upgrade itself to a tracker"
                    _logger.LogInformation("[Consensus Role] Acting as passive responder to Tracker {TrackerId}. No self-promotion or outbound loop launched.", senderNodeId);
    
                    _activeSession = new ConsensusMergeSession()
                    {
                        Type = ConsensusSessionType.Peer, // Mark as peer session
                        CurrentTargetNodeId = senderNodeId
                    };
                    _activeSession.CurrentCSR = _consensusStartRequests[senderNodeId];
                    _consensusStartRequests.Clear();
                    _consensusStartRequests[senderNodeId] = _activeSession.CurrentCSR!;

                    lock (_epochLock)
                    {
                        //record today`s epoch.. jsut in case we step over the midnight (haha)
                        _currentEpochDayKey = utcNow.ToString("yyyy-MM-dd");
                    }
                    await StartConsensusMergeSessionAsync(utcNow);
                    RegisterNegotiationNetworkCommandHandlers();
                    RegisterPeerNetworkCommandHandlers();

                    ConsensusStarted?.Invoke();
                    return "STARTING_UP";
                }
                else
                {
                    // Request is from a PEER, and we are also a PEER
                    if (hasActiveTrackerConnection)
                    {
                        // "otherwise return an available tracker"
                        var activeTrackerNode = lastSeenNodes
                            .FirstOrDefault(c => c.Identity.Roles.Contains("tracker") &&  c.Identity.SignalingEndpoints.Any() && c.LastSeen.AddMinutes(5) > DateTime.UtcNow);
                        string trackerAddress = activeTrackerNode != null ? activeTrackerNode.Identity.SignalingEndpoints?.FirstOrDefault() ?? "no-active-tracker-cached" : "no-active-tracker-cached";

                        _logger.LogInformation("[Consensus Role] Both are peers, tracker is available. Redirecting Peer {Id} to Tracker {Address}.", senderNodeId, trackerAddress);
                        _activeSession = null;
                        return $"REDIRECT_TO_TRACKER|{trackerAddress}";
                    }
                    else
                    {
                        // "promote to a promoted_tracker if there are no trackers around and enact normal tracker loop activities"
                        _logger.LogWarning("[Consensus Role] No trackers around! Promoting ourselves to promoted_tracker and launching normal loop.");

                        var roles = _identity.GetCapacities();
                        if (!roles.Contains("promoted_tracker"))
                        {
                            roles.Add("promoted_tracker");
                            var roleName = nameof(_p2pSettings.CurrentValue.NodeRoles);

                            using (var scope = _scopeFactory.CreateScope())
                            {
                                var settingSevice = scope.ServiceProvider.GetRequiredService<ISettingService>();
                                await settingSevice.SetAsync(P2PModuleEntryPoint.MODULE_ID, $"{nameof(P2PSettings)}:{roleName}", roles);
                            }
                            _identity.TriggerUpdate();
                        }

                        isLocalNodeTracker = true; // Act as tracker now
                    }
                }
            }

            // Standard Tracker / Promoted Tracker Session Setup
            _activeSession = new ConsensusMergeSession()
            {
                Type = ConsensusSessionType.Tracker,
                CurrentTargetNodeId = senderNodeId
            };

            _activeSession.CurrentCSR = _consensusStartRequests[senderNodeId];

            // REQUIRED SECURITY CHECK: Out-of-Time Rating & Threshold Verification Gate
            bool requiredTimeDemoteCheck = _activeSession.CurrentCSR != null && _activeSession.CurrentCSR.TimeStamp.Hour != 23;
            if (requiredTimeDemoteCheck && _activeSession.CurrentCSR != null)
            {
                var initiatorNode = GetWeightedIterationNode(_activeSession.CurrentCSR.NodeIdFrom);
                double thresholdValue = _ratingSettings.CurrentValue.ConsensusOutOfTImeStartThreshold;

                // 1. Verify and penalize the immediate direct neighbor (Y) for relaying out-of-time traffic
                var directRelayerNode = _peerTracker.GetPeer(senderNodeId);
                if (directRelayerNode != null)
                {
                    _logger.LogWarning("[Security Gate] Charging Relayer Neighbor Y ({Id}) trust cost for processing out-of-time loop.", senderNodeId);
                    await _peerTracker.UpdateTrustAsync(senderNodeId, -5);
                }

                // 2. Evaluate the original Cryptographic Initiator Source (Z)
                if (initiatorNode != null)
                {
                    if (initiatorNode.Disbelief > 0.5 || initiatorNode.NodeData.TrustScore < thresholdValue)
                    {
                        _logger.LogCritical("[Security Gate] Aborted session init. Out-of-time requester Z {Id} rating ({Score}) is below boundary limit ({Threshold}).",
                            _activeSession.CurrentCSR.NodeIdFrom, initiatorNode.NodeData.TrustScore, thresholdValue);
                        _consensusStartRequests.Clear();
                        _activeSession = null;
                        return "REJECT_REPUTATION_INSUFFICIENT";
                    }

                    // Clemency Pass: If the node is highly unfamiliar or faded, bypass upfront rating penalty
                    bool isUnfamiliarOrFaded = initiatorNode.Uncertainty > 0.7;
                    if (isUnfamiliarOrFaded)
                    {
                        _logger.LogInformation("[Security Gate] Unfamiliar source Z {Id} detected via high uncertainty. Granting clean bypass pass for epidemic convergence.",
                            _activeSession.CurrentCSR.NodeIdFrom);
                        _activeSession.CurrentCSR.StartingRating = initiatorNode.NodeData.TrustScore;
                    }
                    else
                    {
                        // Apply proportional trust decay upfront
                        double initialCostRate = 0.10;
                        initiatorNode.Belief *= (1.0 - initialCostRate);
                        initiatorNode.Disbelief = (initiatorNode.Disbelief * (1.0 - initialCostRate)) + initialCostRate;
                        initiatorNode.Uncertainty = Math.Max(0.0, 1.0 - (initiatorNode.Belief + initiatorNode.Disbelief));

                        double currentExpectation = initiatorNode.Belief + (0.5 * initiatorNode.Uncertainty);
                        initiatorNode.NodeData.TrustScore = (int)Math.Clamp(Math.Floor(-50.0 + (150.0 * currentExpectation)), -50, 100);

                        _activeSession.CurrentCSR.StartingRating = initiatorNode.NodeData.TrustScore;
                    }
                }
                else
                {
                    _logger.LogInformation("[Security Gate] Initializing completely new source identity Z: {Id} at safe neutral baseline (25).", _activeSession.CurrentCSR.NodeIdFrom);
                    _activeSession.CurrentCSR.StartingRating = 25;
                    requiredTimeDemoteCheck = false;
                }
            }

            _consensusStartRequests.Clear();
            _consensusStartRequests[senderNodeId] = _activeSession.CurrentCSR!;
            ConsensusStarted?.Invoke();

            _activeSession.Control.OrchestrationCts?.Cancel();
            _activeSession.Control.OrchestrationCts = new CancellationTokenSource();
            var token = _activeSession.Control.OrchestrationCts.Token;

            // Enforce Peer Request Check:
            // "if request is from a peer when we are in 'tracker' mode - do not start consensus merge from self with it - only provide data and sign stuff for it, nothing more"
            if (isLocalNodeTracker && !isSenderTracker)
            {
                _logger.LogInformation("[Consensus Role] Peer request on Tracker. We accept to host, but will NOT start outbound tree-merge.");
            }
            else
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await RunTreeOrchestrationLoopAsync(utcNow, token);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "[Consensus Coordinator] Orchestration loop crashed.");
                        await AbortConsensusMergeAsync();
                        Cleanup();
                    }
                }, token);
            }

            return "STARTING_UP";
        }

        if (_activeSession == null)
            return "WTF";

        var session = _activeSession;

        if (session.Control.ProcessingLock != 0 || !string.IsNullOrEmpty(session.CurrentTargetNodeId))
        {
            return $"BUSY|{session.CurrentTargetNodeId}|LOCK_ACTIVE";
        }

        if (session.Deltas.DeltasToSync.Count > 0)
        {
            _logger.LogInformation("[Consensus Coordinator] Pending unmerged deltas on disk. Forcing delta consolidation first.");
            return $"BUSY|{session.Deltas.DeltasToSync.Count}|DELTAS_PENDING";
        }

        if (session.PreviousSyncSet.Contains(senderNodeId))
        {
            return "ALREADY_SYNCED";
        }

        if (envelope.CommandType == "ConsensusMergeInitRequest")
        {
            lock (_epochLock)
            {
                session.CurrentTargetNodeId = senderNodeId;
            }
        }

        return "AVAILABLE";
    }


    /// <summary>
    /// Handles ConsensusStartSignal. Active peers receiving this signal initialize a passive 
    /// Peer session and process delta gossip packets as they are received.
    /// </summary>
    private async Task<string> PeerConsensusStartMessage_Handler(string senderNodeId, P2PPacketEnvelope envelope, CancellationToken token)
    {
        var request = JsonSerializer.Deserialize<ConsensusRequest>(envelope.SerializedPayload);
        ConsensusRequest? req = null;
        if (request != null && !string.IsNullOrEmpty(request.NodeIdFrom))
        {
            req = request;
        }

        var utcNow = DateTime.UtcNow;
        var localRoles = _identity.GetCapacities();
        bool isLocalNodeTracker = localRoles.Contains("tracker") || localRoles.Contains("promoted_tracker");

        if (!isLocalNodeTracker)
        {
            _logger.LogInformation("[Consensus Signaling] Received Consensus Start Signal. Initializing passive Peer session...");

            lock (_epochLock)
            {
                if (_activeSession == null)
                {
                    _activeSession = new ConsensusMergeSession()
                    {
                        Type = ConsensusSessionType.Peer,
                        CurrentTargetNodeId = senderNodeId,
                        CurrentCSR = req,
                        DayKey = utcNow.ToString("yyyy-MM-dd")
                    };
                }

                if(!_receivedStartSignal) { 
                    _receivedStartSignal = true;
                    _receivedDeltaGossip = false; // Reset to track upcoming gossip waves
                    _receivedEndSignal = false; // Reset to track upcoming gossip waves
                    ConsensusStarted?.Invoke();

                    var cts = new CancellationTokenSource();
                    _activeSession.Control.OrchestrationCts = cts;

                    // Capture essential state parameters for use inside the background watchdog thread scope
                    string workingDayKey = _activeSession.DayKey;
                    string trackerId = senderNodeId;

                    // Spawns a background WATCHDOG task to police tracker silence gates
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            _logger.LogInformation("[Consensus Watchdog] Passive delta grace tracker initialized.");
                            DateTime lastActivityTimestamp = DateTime.UtcNow;

                            while (_activeSession != null &&
                                   _activeSession.Type == ConsensusSessionType.Peer &&
                                   !cts.Token.IsCancellationRequested)
                            {
                                // If a delta or end signal was received via signal handlers, reset our activity watch marker
                                if (_receivedDeltaGossip || _receivedEndSignal)
                                {
                                    lastActivityTimestamp = DateTime.UtcNow;
                                    _receivedDeltaGossip = false; // Reset flag to wait for subsequent packets
                                }

                                // SILENT TRACKER ABUSE GUARD: Check if the tracker went silent for more than 5 minutes
                                if ((DateTime.UtcNow - lastActivityTimestamp).TotalMinutes >= 5.0)
                                {
                                    _logger.LogWarning("[Consensus Watchdog] Tracker {Id} breached 5-minute delta grace period! Enacting emergency bypass.", trackerId);

                                    // Dock the silent tracker's reputation score immediately
                                    await _peerTracker.UpdateTrustAsync(trackerId, -15);

                                    // Find alternative trackers in the network matrix to rescue current daily convergence
                                    var alternateTrackers = _peerTracker.GetLastSeenNodes(PeerTracker.DefaultNodeComparer)
                                        .Where(n => n.Identity.NodeId != trackerId && (n.Identity.Roles.Contains("tracker") || n.Identity.Roles.Contains("promoted_tracker")))
                                        .ToList();

                                    List<PeerNodeHistoric> workingTrackers = new();

                                    foreach (var alt in alternateTrackers)
                                    {
                                        if (workingTrackers.Count > 2) break;
                                        if (!alt.Identity.SignalingEndpoints.Any()) continue;

                                        string targetEndp = string.Empty;
                                        List<string> activeEndpoints = new();
                                        foreach (var endpoint in alt.Identity.SignalingEndpoints)
                                        {
                                            targetEndp = string.Empty;
                                            if (!string.IsNullOrEmpty(targetEndp = await _conenctionService.GetEndpointActiveAsync(endpoint)))
                                            {
                                                activeEndpoints.Add(targetEndp);
                                            }
                                            //if (await _conenctionService.GetEndpointActiveAsync(endpoint))
                                            //{
                                            //    activeEndpoints.Add(endpoint);
                                            //}
                                        }

                                        if (activeEndpoints.Any())
                                        {
                                            workingTrackers.Add(alt);
                                        }
                                    }

                                    workingTrackers = workingTrackers.Take(3).ToList();

                                    if (workingTrackers.Any())
                                    {
                                        _logger.LogInformation("[Consensus Watchdog] Found {Count} alternate trackers. Fetching and merging data from ALL of them...", workingTrackers.Count);

                                        foreach (var workingTracker in workingTrackers)
                                        {
                                            _logger.LogInformation("[Consensus Watchdog] Attempting progressive data rescue pipeline from backup tracker: {Id}", workingTracker.Identity.NodeId);
                                            bool trackerSuccess = false;
                                            int configuredTimeout = _p2pSettings.CurrentValue.DefaultRequestTimeoutSeconds > 0 ? _p2pSettings.CurrentValue.DefaultRequestTimeoutSeconds : 10;
                                            var interChunkTimeout = TimeSpan.FromSeconds(configuredTimeout / 2 > 0 ? configuredTimeout / 2 : 5);
                                            _activeSession.CurrentTargetNodeId = workingTracker.Identity.NodeId;
                                            // Strategy A: Request active, incomplete consensus state via standard chunking
                                            var progressiveTcs = new TaskCompletionSource<bool>();

                                            try
                                            {
                                                await _routerService.SendRequestProgressiveAsync(
                                                    workingTracker.Identity.NodeId,
                                                    "ConsensusMergeRequest",
                                                    "{}",
                                                    async (chunkJson, error, progress, finished) => ConsensusMergeResponseIncoming_Handler(progressiveTcs, chunkJson, error, progress, finished),
                                                    new RouteSettings { Timeout = interChunkTimeout, CancellationToken = cts.Token });

                                                trackerSuccess = await progressiveTcs.Task;
                                            }
                                            catch
                                            {
                                                trackerSuccess = false;
                                            }

                                            // Strategy B Fallback: If Strategy A failed or returned error flags, fallback to cold directory read
                                            if (!trackerSuccess)
                                            {
                                                _logger.LogWarning("[Consensus Watchdog] Progressive merge stream unavailable from tracker {Id}. Falling back to latest finalized segmented read.", workingTracker.Identity.NodeId);

                                                try
                                                {

                                                    var coldTcs = new TaskCompletionSource<bool>();
                                                    await _routerService.SendRequestProgressiveAsync(
                                                       workingTracker.Identity.NodeId,
                                                       "RequestConsensusLatestSegmented",
                                                       JsonSerializer.Serialize(new { BatchSize = 40 }),
                                                       async (chunkJson, error, progress, finished) =>
                                                       {
                                                           if (_activeSession == null)
                                                           {
                                                               _logger.LogInformation("[Consensus Merge] Consensus merge on empty session. Skipping.");
                                                               coldTcs.TrySetResult(false);
                                                               return;
                                                           }

                                                           if (!string.IsNullOrEmpty(error) || string.IsNullOrEmpty(chunkJson) || chunkJson.StartsWith("NOPE") || chunkJson.StartsWith("NO_ITERATION_ACTIVE"))
                                                           {
                                                               if (!string.IsNullOrEmpty(error))
                                                               {
                                                                   _logger.LogInformation("[Consensus Merge]Error on data segment from Tracker {Id}: {error}", _activeSession.CurrentTargetNodeId, error);
                                                                   return;
                                                               }
                                                               coldTcs.TrySetResult(false);
                                                               return;
                                                           }

                                                           if (!string.IsNullOrEmpty(chunkJson))
                                                           {
                                                               var docSegment = JsonSerializer.Deserialize<NodesTransferContainer>(chunkJson);
                                                               if (docSegment != null && docSegment.Nodes != null)
                                                               {
                                                                   StageIncomingNodesAsync(docSegment.Nodes, workingTracker.Identity.NodeId);
                                                               }
                                                           }

                                                           if (finished) coldTcs.TrySetResult(true);
                                                       },
                                                       new RouteSettings
                                                       {
                                                           Timeout = interChunkTimeout,
                                                           CancellationToken = cts.Token
                                                       });// Monitor activity per packet arrival threshold

                                                    trackerSuccess = await coldTcs.Task;
                                                }
                                                catch
                                                {
                                                    trackerSuccess = false;
                                                }
                                            }

                                            if (trackerSuccess)
                                            {
                                                _logger.LogInformation("[Consensus Watchdog] Successfully accumulated data segment from Tracker: {Id}", workingTracker.Identity.NodeId);
                                            }
                                        }
                                    }
                                    else
                                    {
                                        _logger.LogCritical("[Consensus Watchdog] Extreme isolation: No alternative trackers online to download snapshot from. Terminating session.");
                                    }

                                    await CommitTreeMergeAsync();
                                    Cleanup();
                                    break;
                                }

                                // Sleep checking intervals to save CPU cycles
                                await Task.Delay(TimeSpan.FromSeconds(10), cts.Token);
                            }
                        }
                        catch (OperationCanceledException) { 
                        // Clean exit on cleanup triggers 
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "[Consensus Watchdog] Error inside passive delta guard watchdog loop.");
                        }
                    }, cts.Token);
                }
            }
        }
        else
        {
            _logger.LogInformation("[Consensus Signaling] Tracker ignored Consensus Start Signal from Node {SenderId}.", senderNodeId);
        }

        return "AVAILABLE";
    }

    private async Task<string> HandleRequestConsensusDelta_Handler(string senderId, P2PPacketEnvelope envelope, CancellationToken cancelToken)
    {
        if (_activeSession == null)
        {
            await _routerService.SendProgressiveResponseAsync(senderId, envelope.MessageId, envelope.CommandType, "{}", 100, true, "NO_SESSION");
            return string.Empty;
        }

        _ = Task.Run(async () =>
        {
            try
            {  
                var req = JsonSerializer.Deserialize<ConsensusDeltaRequest>(envelope.SerializedPayload);
                if (req == null || string.IsNullOrEmpty(req.DeltaIdentifier))
                {
                    await _routerService.SendProgressiveResponseAsync(senderId, envelope.MessageId, envelope.CommandType, "{}", 100, true, "INVALID_REQUEST");
                    return;
                }

                // Alphanumeric sanity check to prevent directory traversal
                string cleanIdentifier = Path.GetFileName(req.DeltaIdentifier);
                if (cleanIdentifier != req.DeltaIdentifier || !IsAlphanumeric(cleanIdentifier))
                {
                    _logger.LogWarning("[Security] Blocked directory traversal or non-alphanumeric delta identifier: {Id}", req.DeltaIdentifier);
                    await _ratingService.ApplyLocalReputationUpdateAsync(senderId, -15);
                    await _routerService.SendProgressiveResponseAsync(senderId, envelope.MessageId, envelope.CommandType, "{}", 100, true, "TRAVERSAL_DETECTED");
                    return;
                }

                string specificDeltaPath = Path.Combine(_activeSession.MergeDeltaDir, cleanIdentifier);

                if (!Directory.Exists(specificDeltaPath))
                {
                    _logger.LogWarning("[Consensus Coordinator] Requested delta directory not found: {Path}", specificDeltaPath);
                    await _routerService.SendProgressiveResponseAsync(senderId, envelope.MessageId, envelope.CommandType, "{}", 100, true, "NOT_FOUND");
                    return;
                }

                // Fetch metadata parameter mappings from metadata.json inside sharded delta folders

                var nodeFiles = Directory.EnumerateFiles(specificDeltaPath, "*.json").ToList();
                int totalNodesCount = nodeFiles.Count;
                int batchSize = 40;
                int totalChunks = (int)Math.Ceiling((double)totalNodesCount / batchSize);

                for (int i = 0; i < totalChunks; i++)
                {
                    var batchFiles = nodeFiles.Skip(i * batchSize).Take(batchSize);
                    var nodesInBatch = new List<PeerNodeHistoric>();

                    foreach (var file in batchFiles)
                    {
                        try
                        {
                            string content = await File.ReadAllTextAsync(file);
                            var weighted = JsonSerializer.Deserialize<WeightedConsensusNode>(content);
                            if (weighted?.NodeData != null) nodesInBatch.Add(weighted.NodeData);
                        }
                        catch { 
                            // skip  
                        }
                    }

                    var docSegment = new ConsensusDeltaDocument
                    {
                        DeltaNodes = nodesInBatch
                    };

                    string chunkSerialized = JsonSerializer.Serialize(docSegment);
                    double progressPercent = (double)(i + 1) / totalChunks * 100.0;
                    bool isCompleted = (i == totalChunks - 1);

                    await _routerService.SendProgressiveResponseAsync(
                        senderId,
                        envelope.MessageId,
                        envelope.CommandType,
                        chunkSerialized,
                        progressPercent,
                        isCompleted);

                    await Task.Delay(5, cancelToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Consensus Coordinator] Exception in 'RequestConsensusDelta' for {Peer}", senderId);
                try { await _routerService.SendProgressiveResponseAsync(senderId, envelope.MessageId, envelope.CommandType, "{}", 100, true, "INTERNAL_ERROR"); } catch { }
            }
        }, cancelToken);

        return string.Empty;
    }

    private async Task<string> ConsensusMergeRequest_Handler(string senderNodeId, P2PPacketEnvelope envelope, CancellationToken cancelToken)
    {
        if (_activeSession == null) {
            await _routerService.SendProgressiveResponseAsync(senderNodeId, envelope.MessageId, envelope.CommandType, "{}", 100, true, "NOPE");
            return string.Empty;
        }

        //progressive
        var op = _currentMergeIteration;
        if (op == null || !Directory.Exists(op.MergeDir))
        {
            await _routerService.SendProgressiveResponseAsync(senderNodeId, envelope.MessageId, envelope.CommandType, "{}", 100, true, "NO_ITERATION_ACTIVE");
            return string.Empty;
        }

        // read file paths
        var nodeFiles = Directory.EnumerateFiles(op.MergeDir, "*.json").ToList();
        int totalNodesCount = nodeFiles.Count;
        int batchSize = 40;
        int totalChunks = (int)Math.Ceiling((double)totalNodesCount / batchSize);

        for (int i = 0; i < totalChunks; i++)
        {
            var batchFiles = nodeFiles.Skip(i * batchSize).Take(batchSize);
            var nodesInBatch = new List<PeerNodeTransferred>();

            foreach (var file in batchFiles)
            {
                try
                {
                    string content = await File.ReadAllTextAsync(file);
                    var weighted = JsonSerializer.Deserialize<WeightedConsensusNode>(content);
                    if (weighted?.NodeData != null)
                    {
                        nodesInBatch.Add(new PeerNodeTransferred
                        {
                            Identity = weighted.NodeData.Identity,
                            TrustScore = weighted.NodeData.TrustScore,
                            LastSeen = weighted.NodeData.LastSeen
                        });

                    }
                }
                catch {  }
            }

            var docSegment = new NodesTransferContainer
            {
                Nodes = nodesInBatch
            };

            string chunkSerialized = JsonSerializer.Serialize(docSegment);
            double progressPercent = (double)(i + 1) / totalChunks * 100.0;
            bool isCompleted = (i == totalChunks - 1);

            await _routerService.SendProgressiveResponseAsync(
                senderNodeId,
                envelope.MessageId,
                envelope.CommandType,
                chunkSerialized,
                progressPercent,
                isCompleted);

            await Task.Delay(50, cancelToken);
        }

        return string.Empty;
    }

    #region REUSABLE SESSION-LEVEL ROUTING & CONSOLIDATION WRAPPERS

    public async Task StartConsensusMergeSessionAsync(DateTime dayKey)
    {
        _logger.LogInformation("[Consensus Coordinator Session] Initializing global session driver for date {DayKey}", dayKey);

        // Step 1: Initialize the Iteration file structure cleanly

        string basePath = _p2pSettings.CurrentValue.DataStoragePath ?? "./Module_Data/P2P";
        string baseMergeDir = Path.Combine(basePath, "consensus_merges", "active");

        lock (_epochLock)
        {
            if(_activeSession == null)
            {
                _activeSession = new ConsensusMergeSession
                {
                };
            }

            _activeSession.DayKey = dayKey.ToString("yyyy-MM-dd");
            _activeSession.DayDateKey = dayKey.Date;

            _activeSession.BaseMergeDir = baseMergeDir;
            _activeSession.DayKeyPrivateKey = _identity.PrivateKey;
            _activeSession.DayKeyPublicKey = _identity.PublicKey;
            _activeSession.DayKeySignatureKey = _identity.PrivateSignatureKey;
            _activeSession.DayKeyVerifyKey = _identity.PublicVerifyKey;
            //chain signature for deltas
            _activeSession.ChainSignatureLastNode = _identity.NodeId;
            _activeSession.ChainSignaturesLastNodeVerify = _identity.PublicVerifyKey;

            _activeSession.MergeDeltaDir = Path.Combine(baseMergeDir, "ConsensusCurrentMergeDelta");
            _activeSession.Deltas.IncomingDeltasDir = Path.Combine(baseMergeDir, "ConsensusIncomingDeltas");
            _activeSession.PreviousSyncSet = new HashSet<string> { _identity.NodeId };
            _activeSession.CurrentSyncSet = new HashSet<string> { _identity.NodeId };
        }

        var seed = new ChainSignature
        {
            Order = 0,
            NodeId = _identity.NodeId
        };

        seed.Signature = P2PCrypto.SignData(_identity.NodeId, _identity.PrivateSignatureKey);

        _activeSession.Deltas.OurChainSignatures.Prepend(seed);

        await StartConsensusMergeAsync();
        _activeSession.Operation = _currentMergeIteration!;

        Directory.CreateDirectory(_activeSession.MergeDeltaDir);
        Directory.CreateDirectory(_activeSession.Deltas.IncomingDeltasDir);
    }

    #endregion

    /// <summary>
    /// Implements Log-Scale exploration selection and partner connection lock.
    /// Loop-negotiates using visiting queue, rotating BUSY nodes to the tail-end naturally.
    /// </summary>
    private async Task RunTreeOrchestrationLoopAsync(DateTime todayKey, CancellationToken ct)
    {
        lock (_epochLock)
        {
            //record today`s epoch.. jsut in case we step over the midnight (haha)
            _currentEpochDayKey = todayKey.ToString("yyyy-MM-dd");
        }

        await StartConsensusMergeSessionAsync(todayKey);
        RegisterNegotiationNetworkCommandHandlers();

        if (_activeSession == null) return;
        var session = _activeSession;

        bool requiredTimeDemoteCheck = _activeSession.CurrentCSR != null && _activeSession.CurrentCSR.TimeStamp.Hour != 23;
        if (_activeSession.CurrentCSR != null && requiredTimeDemoteCheck)
        {
            var senderNode = GetWeightedIterationNode(_activeSession.CurrentCSR.NodeIdFrom);
            double thresholdValue = _ratingSettings.CurrentValue.ConsensusOutOfTImeStartThreshold;

            if (_activeSession.CurrentCSR.NonTransitive && senderNode != null)
            {
                // Verify the node still clears the threshold bar before integrating any updates
                if (senderNode.NodeData.TrustScore < thresholdValue)
                {
                    _logger.LogCritical("[Security Backlash] Core verification caught out-of-time node {Id} below minimal score threshold. Aborting iteration.", _activeSession.CurrentCSR.NodeIdFrom);
                    await _ratingService.ApplyLocalReputationUpdateAsync(_activeSession.CurrentCSR.NodeIdFrom, -_ratingSettings.CurrentValue.ConsensusOutOfTImePrice);
                    await AbortConsensusMergeAsync();
                    Cleanup();
                    throw new Exception("DOS attack: Insufficient reputation scores discovered under out-of-time loop processing.");
                }

                _logger.LogWarning("[Security Backlash] Node {Id} verified outside time boundaries. Applying trust penalty.", _activeSession.CurrentCSR.NodeIdFrom);

                if (senderNode.NodeData.LastSeen.Date == DateTime.UtcNow.Date)
                {
                    // we punish the requester with proportional decay
                    double penaltyRate = _ratingSettings.CurrentValue.ConsensusOutOfTImePrice / 150.0;

                    senderNode.Belief = senderNode.Belief * (1.0 - penaltyRate);
                    senderNode.Disbelief = (senderNode.Disbelief * (1.0 - penaltyRate)) + penaltyRate;
                    senderNode.Uncertainty = Math.Max(0.0, 1.0 - (senderNode.Belief + senderNode.Disbelief));

                    double currentExpectation = senderNode.Belief + (0.5 * senderNode.Uncertainty);
                    senderNode.NodeData.TrustScore = (int)Math.Clamp(Math.Floor(-50.0 + (150.0 * currentExpectation)), -50, 100);

                    string badSenderNodePath = Path.Combine(session.Operation.MergeDir, $"{senderNode.NodeData.Identity.NodeId}.json");

                    // Standardized sharded node write back to active IterDir folder
                    File.WriteAllText(badSenderNodePath, JsonSerializer.Serialize(senderNode, new JsonSerializerOptions { WriteIndented = true }));
                }
                else
                {
                    _logger.LogWarning("[Security Backlash] Node {Id} attempted to initiate consensus outside time bounds and sent some shite. Demoting trust.", _activeSession.CurrentCSR.NodeIdFrom);

                    await _ratingService.ApplyLocalReputationUpdateAsync(_activeSession.CurrentCSR.NodeIdFrom, -_ratingSettings.CurrentValue.ConsensusOutOfTImePrice);
                    await AbortConsensusMergeAsync();
                    Cleanup();
                    throw new Exception("DOS attack");
                }
            }
            else
            {
                // _activeSession.CurrentCSR is transitive or we do not have the sender node yet
                // Postpone punish until we receive data for the node in the upcoming chunk streams
                requiredTimeDemoteCheck = true;
            }
        }


        var localId = _identity.GetLocalNodeId();
        var candidateNodes = _peerTracker.GetLastSeenNodes(PeerTracker.DefaultNodeComparer).ToList();

        // Initialize the tracking visit queue
        session.NodesToVisit.Clear();
        foreach (var peer in candidateNodes)
        {
            if (peer.Identity.NodeId != localId && !string.IsNullOrEmpty(peer.Identity.NodeId)
                && (peer.Identity.Roles.Contains("tracker") || peer.Identity.Roles.Contains("promoted_tracker")))
            {
                session.NodesToVisit.Enqueue(peer.Identity.NodeId);
            }
        }

        //no trackers seen
        //promote ourselves to tracker_promoted if we are not one
        //available trackers? demote outselves from promoted_tracker
        if (!session.NodesToVisit.Any())
        {
            //no trackers seen
            //promote ourselves
            var roleAvailable = _identity.HasRole("tracker") || _identity.HasRole("promoted_tracker");
            if (!roleAvailable)
            {
                var roles = _identity.GetCapacities();
                roles.Add("promoted_tracker");
                var roleName = nameof(_p2pSettings.CurrentValue.NodeRoles);
                using (var scope = _scopeFactory.CreateScope())
                {
                    var settingSevice = scope.ServiceProvider.GetRequiredService<ISettingService>();
                    await settingSevice.SetAsync(P2PModuleEntryPoint.MODULE_ID, $"{nameof(P2PSettings)}:{roleName}", roles);
                }

                _identity.TriggerUpdate();
            }

            session.NodesToVisit.Clear();
            foreach (var peer in candidateNodes)
            {
                if (peer.Identity.NodeId != localId && !string.IsNullOrEmpty(peer.Identity.NodeId))
                {
                    session.NodesToVisit.Enqueue(peer.Identity.NodeId);
                }
            }
        }
        else
        {
            var roleAvailable = _identity.HasRole("promoted_tracker");
            if (roleAvailable)
            {
                //we have traker, demote ourselves
                var roles = _identity.GetCapacities();
                roles.Remove("promoted_tracker");
                var roleName = nameof(_p2pSettings.CurrentValue.NodeRoles);

                using (var scope = _scopeFactory.CreateScope())
                {
                    var settingSevice = scope.ServiceProvider.GetRequiredService<ISettingService>();
                    await settingSevice.SetAsync(P2PModuleEntryPoint.MODULE_ID, $"{nameof(P2PSettings)}:{roleName}", roles);
                }

                _identity.TriggerUpdate();
                return;
            }
        }

        _logger.LogInformation("[Consensus Coordinator] Starting set merge loop. Nodes in queue: {Count}", session.NodesToVisit.Count);


        if (session.MyCSR == null)
        {
            var myCSR = new ConsensusRequest
            {
                NodeIdFrom = _identity.NodeId,
                TimeStamp = DateTime.UtcNow,
                NonTransitive = true,
                Nonce = Guid.NewGuid().ToString("n")
            };

            if (session.CurrentCSR != null) myCSR.NonTransitive = false;

            myCSR.Signature = P2PCrypto.SignData(myCSR.GetHash(), _activeSession.DayKeySignatureKey);

            session.MyCSR = myCSR;
        }
        //we started it
        if (session.CurrentCSR == null) session.CurrentCSR = session.MyCSR;

        // Fire initial signed ConsensusStartSignal broadcast down connected active channels
        await BroadcastStartConsensusSignalAsync(session.CurrentCSR);

        while ((session.NodesToVisit.Count > 0 || session.BusyNodes.Count > 0
                || session.Deltas.DeltasToSync.Count > 0) && !ct.IsCancellationRequested)
        {
            if (session.Control.ProcessingLock != 0)
            {
                //we are locked with someone already and in processing, wait
                await Task.Delay(250, ct);
                continue;
            }

            string targetNodeId = string.Empty;
            if (string.IsNullOrEmpty(session.CurrentTargetNodeId))
            {
                //select a node
                if (session.NodesToVisit.Count > 0)
                {
                    targetNodeId = session.NodesToVisit.Dequeue();
                }
                else
                {
                    targetNodeId = session.BusyNodes.Dequeue();
                }

                session.NodesVisited.Add(targetNodeId);

                // Skip on a whim if we already merged with this node's set branch
                if (session.PreviousSyncSet.Contains(targetNodeId))
                {
                    _logger.LogDebug("[Consensus Coordinator] Skipping {NodeId} - already present in current synced set.", targetNodeId);
                    continue;
                }
            }

            var peer = GetWeightedIterationNode(targetNodeId);
            if (peer == null) continue; //we cannot connect to that peer


            var initResult = await TrySendInitRequest(session, _activeSession.CurrentCSR, targetNodeId);

            if (string.IsNullOrEmpty(initResult)) continue;

            if (initResult == "FUCK_THIS_REQUEST" ||
                initResult == "FUCK_YOU_ARE" ||
                initResult == "FUCK_TIMESTAMP")
            {
                initResult = await TrySendInitRequest(session, _activeSession.MyCSR, targetNodeId);
            }

            if (initResult == "STARTING_UP")
            {
                session.NodesToVisit.Enqueue(targetNodeId);
                continue;
            }
            else
            if (initResult == "ALREADY_SYNCED")
            {
                continue;
            }
            else
            if (initResult == "AVAILABLE")
            {
                session.CurrentTargetNodeId = targetNodeId;
            }
            else
            if (initResult.StartsWith("BUSY"))
            {
                session.BusyNodes.Enqueue(targetNodeId);
                continue;
            }
            else
            {
                continue;
            }

            //lock in
            Interlocked.CompareExchange(ref session.Control.ProcessingLock, 1, 0);

            _activeSession.Control.NodeMergeCt?.Cancel();
            var cts = new CancellationTokenSource();
            _activeSession.Control.NodeMergeCt = cts;
            var localToken = cts.Token;

            var exchangeComplete = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            _ = Task.Run(async () =>
            {
                try
                {
                    var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

                    int configuredTimeout = _p2pSettings.CurrentValue.DefaultRequestTimeoutSeconds > 0 ? _p2pSettings.CurrentValue.DefaultRequestTimeoutSeconds : 10;
                    var interChunkTimeout = TimeSpan.FromSeconds(configuredTimeout / 2 > 0 ? configuredTimeout / 2 : 5);

                    //request for data
                    await _routerService.SendRequestProgressiveAsync(
                        session.CurrentTargetNodeId,
                        "ConsensusMergeRequest",
                        "{}",
                        async (chunkJson, error, progress, finished) => ConsensusMergeResponseIncoming_Handler(tcs, chunkJson, error, progress, finished),
                        new RouteSettings { Timeout = interChunkTimeout, CancellationToken = localToken } // Monitor activity per packet arrival threshold
                    );

                    //we do nto really care about result, even partial exchange is good enough
                    await tcs.Task;

                    //sign our shite
                    var chain = new ConsensusSignatureChain
                    {
                        ChainSignatures = session.Deltas.OurChainSignatures
                    };

                    var nodeId = session.Deltas.OurChainSignatures.First().NodeId;
                    if (nodeId == _identity.NodeId)
                    {
                        //our signature is initial
                        chain.LastVerifyKey = _identity.PublicVerifyKey;
                    }
                    else
                    {
                        //another node has signed us
                        var node = GetWeightedIterationNode(nodeId);
                        if(node!= null && node.NodeData.Identity.SignatureTime.Date == DateTime.UtcNow.Date)
                        {
                            //verify key for the other node
                            chain.LastVerifyKey = node.NodeData.Identity.PublicVerifyKey;
                        }
                        else
                        {
                            //wtf, how did we end up here?
                            //this still does not stop us from signing
                            //response to another node
                            //even if we got sent some shite
                            await ResetIterationMergeAsync();
                            //it is totally another node`s problem
                            //to sign their consensus and keep in check
                            //so we just drop the shite incoming data
                            exchangeComplete.SetResult(false);
                            return;
                        }
                    }

                    //sign ples
                    var result = await _routerService.SendRequestAsync(targetNodeId, "ConsensusSignature", JsonSerializer.Serialize(chain));

                    if (result.IsSuccess && result.Value != null)
                    {
                        var finalSignature = JsonSerializer.Deserialize<ConsensusSignatureResponse>(result.Value);
                        if (finalSignature != null)
                        {
                            //check the signature
                            var node = GetWeightedIterationNode(session.CurrentTargetNodeId)!;
                            var ourSig = session.Deltas.OurChainSignatures[0];
                            var tohash = targetNodeId + ourSig.NodeId + ourSig.Signature;

                            if (P2PCrypto.TestSignature(tohash, node.NodeData.Identity.PublicVerifyKey, finalSignature.Signature))
                            {
                                //it was signed correctly?
                                session.Deltas.OurChainSignatures.Prepend(new ChainSignature
                                {
                                    Order = session.Deltas.OurChainSignatures.Count,
                                    NodeId = targetNodeId,
                                    Signature = finalSignature.Signature,
                                });
                                //we have that node otherwise we would not be able to connect
                                session.ChainSignatureLastNode = node.NodeData.Identity.NodeId;
                                session.ChainSignaturesLastNodeVerify = node.NodeData.Identity.PublicVerifyKey;

                                lock (_activeSession.Deltas.DeltaLock)
                                {
                                    if (_activeSession.Deltas.PendingDeltas.TryGetValue(targetNodeId, out var deltaPending))
                                    {
                                        deltaPending.Delta.SignatureChain.Clear();
                                        deltaPending.Delta.SignatureChain.AddRange(session.Deltas.OurChainSignatures);
                                        deltaPending.Delta.DeltaVerifyKey = node.NodeData.Identity.PublicVerifyKey;
                                    }
                                    else
                                    {
                                        deltaPending = new ConsensusDeltaPingPending
                                        {
                                            Delta = new ConsensusDeltaPing
                                            {
                                                DeltaFromNode = targetNodeId,
                                                DeltaVerifyKey = node.NodeData.Identity.PublicVerifyKey
                                            }
                                        };
                                        deltaPending.Delta.SignatureChain.AddRange(session.Deltas.OurChainSignatures);
                                        deltaPending.TargetNodes.UnionWith(_activeSession.PreviousSyncSet);
                                    }
                                }
                            }
                            else
                            {
                                //u fuking drunk or smth?
                                await _ratingService.ApplyLocalReputationUpdateAsync(targetNodeId, -2);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[Consensus Coordinator] Node merge task threw an exception targeting {Target}", session.CurrentTargetNodeId);
                }
                finally
                {
                    session.CurrentSyncSet.Add(session.CurrentTargetNodeId);
                    cts.Cancel();
                    cts.Dispose();
                    var nodeMergeCt = _activeSession.Control.NodeMergeCt;
                    Interlocked.CompareExchange(ref nodeMergeCt, null, cts);
                    //unblock session
                    Interlocked.CompareExchange(ref session.Control.ProcessingLock, 0, 1);
                    exchangeComplete.SetResult(true);
                }
            }, localToken);

            var result = await exchangeComplete.Task;

            if (requiredTimeDemoteCheck)
            {
                ////[out_of_time_demotion]
                if (_activeSession.CurrentCSR != null && _activeSession.CurrentCSR.NodeIdFrom != _identity.NodeId)
                {
                    var senderNode = GetWeightedIterationNode(_activeSession.CurrentCSR.NodeIdFrom);
                    double thresholdValue = _ratingSettings.CurrentValue.ConsensusOutOfTImeStartThreshold;
                    _logger.LogWarning("[Security Backlash] Node {Id} re-checking out-of-time consensus boundaries.", _activeSession.CurrentCSR.NodeIdFrom);

                    if (senderNode != null && senderNode.NodeData.LastSeen.Date == DateTime.UtcNow.Date)
                    {
                        // Double check current threshold constraints or look inside the recorded starting rating context
                        if (senderNode.NodeData.TrustScore < thresholdValue || _activeSession.CurrentCSR.StartingRating < thresholdValue)
                        {
                            _logger.LogCritical("[Security Backlash] Core verification loop caught out-of-time node {Id} below minimal score threshold. Aborting iteration.", _activeSession.CurrentCSR.NodeIdFrom);
                            await _ratingService.ApplyLocalReputationUpdateAsync(_activeSession.CurrentCSR.NodeIdFrom, -_ratingSettings.CurrentValue.ConsensusOutOfTImePrice);
                            await AbortConsensusMergeAsync();
                            Cleanup();
                            throw new Exception("DOS attack: Insufficient reputation scores discovered under out-of-time loop processing.");
                        }

                        _logger.LogWarning("[Security Backlash] Node {Id} verified outside time boundaries. Applying primary trust demotion cost rate.", _activeSession.CurrentCSR.NodeIdFrom);

                        // we punish the requester proportionally
                        double penaltyRate = _ratingSettings.CurrentValue.ConsensusOutOfTImePrice / 150.0;

                        senderNode.Belief = senderNode.Belief * (1.0 - penaltyRate);
                        senderNode.Disbelief = (senderNode.Disbelief * (1.0 - penaltyRate)) + penaltyRate;
                        senderNode.Uncertainty = Math.Max(0.0, 1.0 - (senderNode.Belief + senderNode.Disbelief));

                        double currentExpectation = senderNode.Belief + (0.5 * senderNode.Uncertainty);
                        senderNode.NodeData.TrustScore = (int)Math.Clamp(Math.Floor(-50.0 + (150.0 * currentExpectation)), -50, 100);

                        // Proportionally downrate the transient state inside the ongoing document trade context as well
                        _activeSession.CurrentCSR.StartingRating = senderNode.NodeData.TrustScore;

                        string badSenderNodePath = Path.Combine(session.Operation.IterDir, $"{senderNode.NodeData.Identity.NodeId}.json");

                        // Standardized sharded node write back to active IterDir folder
                        File.WriteAllText(badSenderNodePath, JsonSerializer.Serialize(senderNode, new JsonSerializerOptions { WriteIndented = true }));
                    }
                    else
                    {
                        _logger.LogWarning("[Security Backlash] Node {Id} attempted out of bounds execution and passed malicious payload artifacts. Aborting.", _activeSession.CurrentCSR.NodeIdFrom);
                        await _ratingService.ApplyLocalReputationUpdateAsync(_activeSession.CurrentCSR.NodeIdFrom, -_ratingSettings.CurrentValue.ConsensusOutOfTImePrice);
                        await AbortConsensusMergeAsync();
                        Cleanup();
                        throw new Exception("DOS attack: Corrupted payload definitions released from unverified identity.");
                    }
                }
                requiredTimeDemoteCheck = false;
            }

            //outgoing deltas generation and signalling
            var deltaName = await GenerateIterationDeltaAsync(sendRating: false);
            var sigsOrdered = session.Deltas.OurChainSignatures.OrderByDescending(s => s.Order).ToArray();

            bool abortIteration = false;
            lock (_activeSession.Deltas.DeltaLock)
            {
                if (_activeSession.Deltas.PendingDeltas.TryGetValue(targetNodeId, out var deltaPending))
                {
                    deltaPending.Delta.DeltaIdentifier = deltaName;

                    if (deltaPending.Delta.SourceSignatureChain.Any() && deltaPending.Delta.SignatureChain.Any() && !string.IsNullOrEmpty(deltaPending.Delta.DeltaIdentifier))
                    {
                        //we need to send this
                        SendOutDeltas(deltaPending);
                    }
                }
                else
                {
                    abortIteration = true;
                    //we need signature, no signature = no stuff
                }
            }

            if(abortIteration)
            {
                await ResetIterationMergeAsync();
            }


            //incoming deltas sync
            while (session.Deltas.DeltasToSync.Count > 0)
            {
                string deltaId = session.Deltas.DeltasToSync[0];

                try
                {
                    string incomingDeltaFilePath = Path.Combine(session.Deltas.IncomingDeltasDir, $"{deltaId}.json");
                    if (File.Exists(incomingDeltaFilePath))
                    {
                        // Read and deserialize the sharded disk-level ConsensusDeltaOnDisk transaction envelope
                        string deltaPayloadText = await File.ReadAllTextAsync(incomingDeltaFilePath);
                        ConsensusDeltaOnDisk? deltaOnDisk = null;
                        try { deltaOnDisk = JsonSerializer.Deserialize<ConsensusDeltaOnDisk>(deltaPayloadText); } catch { }

                        if (deltaOnDisk != null)
                        {
                            var deltaDir = Path.Combine(session.Deltas.IncomingDeltasDir, deltaOnDisk.DeltaIdentifier);
                            // Forward delta merge operations back into current active IterDir structures
                            StageIncomingConsensusDeltaAsync(deltaOnDisk, deltaDir, deltaOnDisk.DeltaFromNode);

                            // Clean the loaded delta from disk area
                            try
                            {
                                File.Delete(incomingDeltaFilePath);
                                if (Directory.Exists(deltaDir))
                                {
                                    Directory.Delete(deltaDir, true);
                                }
                            }
                            catch { }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[Consensus Coordinator] Exception staging delta index item: {Id}", deltaId);
                }
                finally
                {
                    lock (session.Deltas.DeltaLock)
                    {
                        session.Deltas.DeltasToSync.Remove(deltaId);
                    }
                }
            }

            if (abortIteration)
            {
                //we had an iteration abort, generate self-signed delta
                deltaName = await GenerateIterationDeltaAsync(sendRating: false);
                sigsOrdered = session.Deltas.OurChainSignatures.OrderByDescending(s => s.Order).ToArray();

                var ourSig = session.Deltas.OurChainSignatures[0];
                var tohash = _identity.NodeId + ourSig.NodeId + ourSig.Signature;
                var signature = P2PCrypto.SignData(tohash, _activeSession.DayKeySignatureKey);

                session.Deltas.OurChainSignatures.Prepend(new ChainSignature
                {
                    NodeId = _identity.NodeId,
                    Order = session.Deltas.OurChainSignatures.Count,
                    Signature = signature
                });

                var deltaPending = new ConsensusDeltaPingPending
                {
                    Delta = new ConsensusDeltaPing
                    {
                        DeltaFromNode = targetNodeId,
                        DeltaIdentifier = deltaName,
                        DeltaVerifyKey = _identity.PublicVerifyKey,
                        SignatureChain = session.Deltas.OurChainSignatures,
                        SourceSignatureChain = session.Deltas.OurChainSignatures,
                    },
                    TargetNodes = session.PreviousSyncSet
                };

                SendOutDeltas(deltaPending);
            }

            FinalizeConsensusIterationMergeAsync();

            session.PreviousSyncSet.UnionWith(session.CurrentSyncSet);

            session.CurrentTargetNodeId = string.Empty;

            //have a rest, duh
            await Task.Delay(50, ct);
        }

        _logger.LogInformation("[Consensus Coordinator] Finished orchestration loop. Finalizing consolidations.");

        await CommitTreeMergeAsync();
        //await FinalizeConsensusMergeAsync();

        // Fired when we exhaust the pool of nodes to sync with
        await BroadcastEndConsensusSignalAsync();

        Cleanup();
    }

    private async Task CommitTreeMergeAsync()
    {
        if(_activeSession != null)
        {
            _currentEpochDayKey = _activeSession.DayKey;
            while (_activeSession.Deltas.DeltasToSync.Count > 0)
            {
                string deltaId = _activeSession.Deltas.DeltasToSync[0];

                try
                {
                    string incomingDeltaFilePath = Path.Combine(_activeSession.Deltas.IncomingDeltasDir, $"{deltaId}.json");
                    if (File.Exists(incomingDeltaFilePath))
                    {
                        // Read and deserialize the sharded disk-level ConsensusDeltaOnDisk transaction envelope
                        string deltaPayloadText = await File.ReadAllTextAsync(incomingDeltaFilePath);
                        ConsensusDeltaOnDisk? deltaOnDisk = null;
                        try { deltaOnDisk = JsonSerializer.Deserialize<ConsensusDeltaOnDisk>(deltaPayloadText); } catch { }

                        if (deltaOnDisk != null)
                        {
                            var deltaDir = Path.Combine(_activeSession.Deltas.IncomingDeltasDir, deltaOnDisk.DeltaIdentifier);
                            // Forward delta merge operations back into current active IterDir structures
                            StageIncomingConsensusDeltaAsync(deltaOnDisk, deltaDir, deltaOnDisk.DeltaFromNode);

                            // Clean the loaded delta from disk area
                            try
                            {
                                File.Delete(incomingDeltaFilePath);
                                if (Directory.Exists(deltaDir))
                                {
                                    Directory.Delete(deltaDir, true);
                                }
                            }
                            catch { }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[Consensus Coordinator] Exception staging delta index item: {Id}", deltaId);
                }
                finally
                {
                    lock (_activeSession.Deltas.DeltaLock)
                    {
                        _activeSession.Deltas.DeltasToSync.Remove(deltaId);
                    }
                }
            }

            await FinalizeConsensusMergeAsync();
            _logger.LogInformation("[Consensus Coordinator] Atomic folder promotion finalized successfully for today.");
        }
    }

    private async void ConsensusMergeResponseIncoming_Handler(TaskCompletionSource<bool> tcs, string? chunkJson, string? error, double progress, bool finished)
    {
        if(_activeSession == null)
        {
            _logger.LogInformation("[Consensus Merge] Consensus merge on empty session. Skipping.");
            tcs.TrySetResult(false);
            return;
        }

        if (!string.IsNullOrEmpty(error) || string.IsNullOrEmpty(chunkJson) || chunkJson.StartsWith("NOPE") || chunkJson.StartsWith("NO_ITERATION_ACTIVE"))
        {
            if (!string.IsNullOrEmpty(error))
            {
                _logger.LogInformation("[Consensus Merge]Error on data segment from Tracker {Id}: {error}", _activeSession.CurrentTargetNodeId, error);
                return;
            }
            tcs.TrySetResult(false);
            return;
        }

        if (!string.IsNullOrEmpty(chunkJson))
        {
            var docSegment = JsonSerializer.Deserialize<NodesTransferContainer>(chunkJson);
            if (docSegment != null && docSegment.Nodes != null)
            {
                var verifiedNodes = StageIncomingNodesAsync(docSegment.Nodes, _activeSession.CurrentTargetNodeId);

                if (_activeSession.CurrentCSR != null && _activeSession.CurrentCSR.StartingRating == null && _activeSession.CurrentCSR.TimeStamp.Hour != 23)
                {
                    var activeInitiatorNode = docSegment.Nodes.FirstOrDefault(n => n.Identity.NodeId == _activeSession.CurrentCSR.NodeIdFrom);
                    if (activeInitiatorNode != null)
                    {
                        // Proportional sync: apply the local session calculated trust reductions directly onto the transferred block
                        _logger.LogInformation("[Consensus Response Manager] Inbound state sync: binding calculated out-of-time penalty scores onto identity records for Node {Id}", activeInitiatorNode.Identity.NodeId);
                        _activeSession.CurrentCSR!.StartingRating = activeInitiatorNode.TrustScore;
                    }
                }

                if (verifiedNodes != null && verifiedNodes.Count > 0)
                { 
                    //add handling for a peer-to-peer consensus

                    foreach (var node in docSegment.Nodes)
                    {
                        if (node.Identity.Roles.Contains("tracker") ||
                            node.Identity.Roles.Contains("promoted_tracker"))
                        {
                            if (!verifiedNodes.Contains(node.Identity.NodeId) ||
                                _activeSession.NodesVisited.Contains(node.Identity.NodeId) ||
                                _activeSession.PreviousSyncSet.Contains(node.Identity.NodeId) ||
                                _activeSession.NodesToVisit.Contains(node.Identity.NodeId))
                            {
                                continue;
                            }
                            else
                            {
                                //update LastSeenByNodeId for connectivity
                                _activeSession.NodesToVisit.Enqueue(node.Identity.NodeId);
                                WeightedConsensusNode? localSessionState = GetWeightedIterationNode(node.Identity.NodeId);
                                var peer = _peerTracker.GetPeer(node.Identity.NodeId);
                                if (peer != null)
                                {
                                    peer.LastSeenByNodeId.Add(_activeSession.CurrentTargetNodeId);
                                    await _peerTracker.UpdatePeerAsync(peer);
                                }
                                else
                                if (localSessionState != null)
                                {
                                    localSessionState.NodeData.LastSeenByNodeId.Add(_activeSession.CurrentTargetNodeId);

                                    var newTracker = new PeerNodeRuntime(localSessionState.NodeData)
                                    {
                                        TrustScore = 0
                                    };

                                    await _peerTracker.UpdatePeerAsync(newTracker);
                                }
                                else
                                {
                                    var newTracker = new PeerNodeRuntime(node)
                                    {
                                        TrustScore = 0,
                                    };
                                    newTracker.LastSeenByNodeId.Add(_activeSession.CurrentTargetNodeId);
                                    await _peerTracker.UpdatePeerAsync(newTracker);
                                }
                            }
                        }
                    }
                }
            }
        }

        if (finished)
        {
            tcs.TrySetResult(true);
        }
    }

    private async Task<string> TrySendInitRequest(ConsensusMergeSession session, ConsensusRequest req, string targetNodeId)
    {
        try
        {
            _logger.LogInformation("[Consensus Coordinator] Sending ConsensusMergeInitRequest targeting peer {Id}", targetNodeId);

            var initResult = await _routerService.SendRequestAsync(
                targetNodeId,
                "ConsensusMergeInitRequest",
                JsonSerializer.Serialize(req),
                new RouteSettings { Timeout = TimeSpan.FromSeconds(15) });

            if (initResult.IsSuccess && !string.IsNullOrEmpty(initResult.Value))
            {
                return initResult.Value;
            }

            _logger.LogWarning("[Consensus Coordinator] Init request failed for target node {NodeId}: {Error}", targetNodeId, initResult.Error);
            return string.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Consensus Coordinator] Error sending handshake init request targeting node {NodeId}.", targetNodeId);
            return string.Empty;
        }
    }

    private async Task<string> HandleConsensusSignatureAsync_Handler(string senderNodeId, P2PPacketEnvelope envelope, CancellationToken token)
    {
        lock (_epochLock)
        {
            if (_activeSession == null) return "FUCK";
        }

        var rawPayload = envelope.SerializedPayload;
        try
        {
            var ping = JsonSerializer.Deserialize<ConsensusSignatureChain>(rawPayload);
            if (ping == null) return "FUCK";

            //only nodes we had business with can request signatures
            if(!_activeSession.PreviousSyncSet.Contains(senderNodeId) || _activeSession.CurrentTargetNodeId != senderNodeId)
            {
                return "NO";
            }

            if(_activeSession.Deltas.DeltaSignatures.TryGetValue(senderNodeId, out var returnSignature))
            {
                //here, take this
                return JsonSerializer.Serialize(new ConsensusSignatureResponse { Signature = returnSignature });
            }

            bool verified = true;
            var sigsOrdered = ping.ChainSignatures.OrderByDescending(i => i.Order).ToList();
            lock (_activeSession.Deltas.DeltaLock)
            {
                if (_activeSession.Deltas.PendingDeltas.ContainsKey(senderNodeId))
                {
                    //if we have already ingested exchange data
                    // Verify signatures inside ChainSignatures using DeltaVerify
                    // since we have ingested data - we should have all the verify keys
                    for (int i = 0; i < sigsOrdered.Count; ++i)
                    {
                        string hashData = sigsOrdered[i].NodeId;
                        if (i + 1 < sigsOrdered.Count)
                        {
                            hashData += sigsOrdered[i + 1].NodeId + Convert.ToHexString(sigsOrdered[i + 1].Signature);
                        }

                        if (i == 0)
                        {
                            //do we have that node already?
                            var node = GetWeightedIterationNode(sigsOrdered[i].NodeId);
                            if (node != null && !P2PCrypto.TestSignature(hashData, node.NodeData.Identity.PublicVerifyKey, sigsOrdered[i].Signature))
                            {
                                verified = false;
                            }

                            if (!verified && P2PCrypto.TestSignature(hashData, ping.LastVerifyKey, sigsOrdered[i].Signature))
                            {
                                //mind your own business
                                verified = true;
                            }
                        }
                        else
                        {
                            //we should have already gotten keys successfully
                            var node = GetWeightedIterationNode(sigsOrdered[i].NodeId);
                            if (node == null)
                            {
                                //it is iccorect, i know, simulating checks
                                P2PCrypto.TestSignature(hashData, ping.LastVerifyKey, sigsOrdered[i].Signature);
                                verified = false;
                            }
                            else
                            if (!P2PCrypto.TestSignature(hashData, node.NodeData.Identity.PublicVerifyKey, sigsOrdered[i].Signature))
                            {
                                verified = false;
                            }
                        }
                    }

                    if (!verified)
                    {
                        _logger.LogWarning("[Security] Discarded DeltaPing from {Peer} - signatures verification failed.", senderNodeId);
                        return "FUCK_SIGNATURE";
                    }
                }
                else
                {
                    //we have not synced yet, sign it anyway
                    //and postpone check for later
                }
            }

            //i signed you, now i expect deltas from you
            if (!_activeSession.Deltas.DeltaChainSignatures.ContainsKey(senderNodeId))
            {
                string tohash = _identity.NodeId + sigsOrdered[0].NodeId + sigsOrdered[0].Signature;
                var signature = P2PCrypto.SignData(tohash, _activeSession.DayKeySignatureKey);

                //we have not signed your shit before
                //we remember you
                //we sign signature in to remember
                lock (_activeSession.Deltas.DeltaLock)
                {
                    _activeSession.Deltas.DeltaSignatures[senderNodeId] = signature;
                    sigsOrdered.Prepend(new ChainSignature
                    {
                        NodeId = _identity.NodeId,
                        Signature = signature,
                        Order = ping.ChainSignatures.Count
                    });
                    _activeSession.Deltas.DeltaChainSignatures[senderNodeId] = sigsOrdered;

                    if (_activeSession.Deltas.PendingDeltas.TryGetValue(senderNodeId, out var deltaPending))
                    {
                        deltaPending.Delta.SourceSignatureChain.Clear();
                        deltaPending.Delta.SourceSignatureChain = sigsOrdered;
                    }
                    else
                    {
                        deltaPending = new ConsensusDeltaPingPending
                        {
                            Delta = new ConsensusDeltaPing
                            {
                                DeltaFromNode = senderNodeId,
                                SourceSignatureChain = sigsOrdered
                            }
                        };
                        deltaPending.TargetNodes.UnionWith(_activeSession.PreviousSyncSet);
                    }

                    if (deltaPending.Delta.SourceSignatureChain.Any() && deltaPending.Delta.SignatureChain.Any() && !string.IsNullOrEmpty(deltaPending.Delta.DeltaIdentifier))
                    {
                        //we need to send this
                        SendOutDeltas(deltaPending);
                    }
                }

                //return the signature
                return JsonSerializer.Serialize(new ConsensusSignatureResponse { Signature = signature });
            }

            //how did we get here?
            return string.Empty;
            //return JsonSerializer.Serialize(new ConsensusSignatureResponse{ Signature = signature });
        }
        catch 
        {

        }
        return string.Empty;
    }

    private async void SendOutDeltas(ConsensusDeltaPingPending deltaPending)
    {
        //send deltas out
        if (_activeSession == null) return;

        lock (_activeSession.Deltas.DeltaLock)
        {
            var deltaCts = new CancellationTokenSource();

            _activeSession.Deltas.DeltaTasks[deltaPending.Delta.DeltaIdentifier] = deltaCts;

            _ = Task.Run(async () =>
            {
                //await _routerService.SendMulticastMessage(session.PreviousSyncSet.ToArray(), "ConsensusDeltaSignalling", );
                var deltaValueSerialized = JsonSerializer.Serialize(deltaPending.Delta);
                //todo: change foreach unicast into multicast
                foreach (var nodeId in deltaPending.TargetNodes)
                {
                    if (deltaCts.Token.IsCancellationRequested) return;
                    await _routerService.SendMessage(nodeId, "ConsensusDeltaSignalling", deltaValueSerialized);
                }

                lock (_activeSession.Deltas.DeltaLock)
                {
                    _activeSession.Deltas.DeltaTasks.Remove(deltaPending.Delta.DeltaIdentifier);
                    _activeSession.Deltas.PendingDeltas.Remove(deltaPending.Delta.DeltaFromNode, out _);
                }

            }, deltaCts.Token);
        }
    }

    private async Task<string> HandleIncomingDeltaSignallingAsync_Handler(string senderNodeId, P2PPacketEnvelope envelope, CancellationToken cancelToken)
    {
        if (_activeSession == null) return "FUCK";

        try
        {
            var ping = JsonSerializer.Deserialize<ConsensusDeltaPing>(envelope.SerializedPayload);
            if (ping == null) return "FUCK";
            
            if(_activeSession.CurrentSyncSet.Contains(ping.DeltaFromNode)) return "SYNCED";

            // Verify signatures inside ChainSignatures using DeltaVerify
            var sigsOrdered = ping.SignatureChain.OrderByDescending(i => i.Order).ToArray();

            bool verified = false;
            if (_activeSession.Deltas.DeltaSignatures.TryGetValue(senderNodeId, out var targetSignature))
            {
                foreach (var deltaInfo in ping.SignatureChain)
                {
                    if (deltaInfo.Signature.SequenceEqual(targetSignature))
                    {
                        verified = true;
                        break;
                    }
                }

                if (verified)
                {
                    //do slightly more complex check later
                    _activeSession.Deltas.DeltaSignatures[senderNodeId] = sigsOrdered[0].Signature;
                    //track delta signatures still
                    _activeSession.Deltas.DeltaNewVerifyKeySignatures[ping.DeltaFromNode] = ping.DeltaVerifyKey; 
                }
                else
                {
                    //yea, okay, we have some other node with signatures that joined
                    //we should have all the verification keys, or it is a bust
                    verified = true;
                    for (int i = 0; i < sigsOrdered.Length; ++i)
                    {
                        string hashData = sigsOrdered[i].NodeId;
                        if (i + 1 < sigsOrdered.Length)
                        {
                            hashData += sigsOrdered[i + 1].NodeId + Convert.ToHexString(sigsOrdered[i + 1].Signature);
                        }

                        if (i == 0)
                        {
                            //if this a new node - we need the keys in delta itself
                            if (!P2PCrypto.TestSignature(hashData, ping.DeltaVerifyKey, sigsOrdered[i].Signature))
                            {
                                verified = false;
                            }
                        }
                        else
                        {
                            byte[]? verifyKey = null;

                            // 1. Try Active Iteration Context
                            var liveNode = GetWeightedIterationNode(sigsOrdered[i].NodeId);
                            if (liveNode != null) verifyKey = liveNode.NodeData.Identity.PublicVerifyKey;

                            // 2. Try Session Ephemeral Discovery
                            if (verifyKey == null && _activeSession.Deltas.DeltaNewVerifyKeySignatures.TryGetValue(sigsOrdered[i].NodeId, out var ephemeralKey))
                                verifyKey = ephemeralKey;

                            // 3. Try Historical Database Fallback
                            if (verifyKey == null)
                            {
                                var historicalNode = _consensusStore.GetHistoricalPeer(_activeSession.DayKey, sigsOrdered[i].NodeId);
                                if (historicalNode != null) verifyKey = historicalNode.Identity.PublicVerifyKey;
                            }

                            // Security Enforcement Gate
                            if (verifyKey == null || !P2PCrypto.TestSignature(hashData, verifyKey, sigsOrdered[i].Signature))
                            {
                                verified = false;
                                // Short-circuit immediately; the historical chain is unverified
                                // no need to simulate checks
                                break; 
                            }
                        }
                    }
                }
            }
            else
            {
                // Peer Role checking fallback
                var localRoles = _identity.GetCapacities();
                bool isLocalNodeTracker = localRoles.Contains("tracker") || localRoles.Contains("promoted_tracker");
                if (!isLocalNodeTracker)
                {
                    // We are a peer, and we received a delta signal. Passively pull from our signaling tracker
                    verified = true;
                    _receivedDeltaGossip = true;
                }
            }

            if (!verified)
            {
                _logger.LogWarning("[Security] Discarded DeltaPing from {Peer} - signatures verification failed.", senderNodeId);
                return "FUCK_SIGNATURE";
            }

            _logger.LogInformation("[Consensus Coordinator] Signature verification succeeded for DeltaPing from {Peer}. Requesting delta progressive transfer...", senderNodeId);

            // Let's trigger outbound progressive pull request targeting the sender node
            int configuredTimeout = _p2pSettings.CurrentValue.DefaultRequestTimeoutSeconds > 0 ? _p2pSettings.CurrentValue.DefaultRequestTimeoutSeconds : 10;
            var interChunkTimeout = TimeSpan.FromSeconds(configuredTimeout / 2 > 0 ? configuredTimeout / 2 : 5);

            // Use the token of active session or create a new linked CTS
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            if (!_activeSession.Deltas.ActiveDeltaDownloads.ContainsKey(ping.DeltaIdentifier))
            {
                // Silently drop duplicate downloads while providing an identical success code to the peer
            }
            else
            {
                var runDownload = Task.Run(async () =>
                {
                    if (_activeSession == null) return;

                    if (_activeSession.CurrentSyncSet.Contains(ping.DeltaFromNode))
                    {
                        return;
                    }
                    string deltaId = string.Empty;
                    try
                    {
                        do
                        {
                            deltaId = GenerateDeltaIdentifierName();
                        }
                        while (File.Exists(Path.Combine(_activeSession.Deltas.IncomingDeltasDir, $"{deltaId}.json")) ||
                                Directory.Exists(Path.Combine(_activeSession.Deltas.IncomingDeltasDir, deltaId)));
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "[Consensus Coordinator] Progressive delta pull failed targeting {Peer}", senderNodeId);
                    }
                    finally
                    {
                        cts.Dispose();
                        _activeSession.Deltas.ActiveDeltaDownloads.Remove(senderNodeId, out var _);
                    }

                    if(string.IsNullOrEmpty(deltaId)) return;

                    var consensusDeltaFile = new ConsensusDeltaOnDisk
                    {
                        DeltaFromNode = senderNodeId,
                        DeltaIdentifier = deltaId // delta on disk
                    };

                    try
                    {
                        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                        var deltaDirPath = Path.Combine(_activeSession.Deltas.IncomingDeltasDir, deltaId);

                        await _routerService.SendRequestProgressiveAsync(
                            senderNodeId,
                            "RequestConsensusDelta",
                            JsonSerializer.Serialize(new ConsensusDeltaRequest { DeltaIdentifier = ping.DeltaIdentifier }),
                            async (chunkJson, error, progress, finished) =>
                            {
                                if (!string.IsNullOrEmpty(error))
                                {
                                    tcs.TrySetException(new Exception(error));
                                    return;
                                }

                                if (_activeSession == null)
                                {
                                    cts.Cancel();
                                    tcs.TrySetResult(false);
                                    return;
                                }

                                if (!string.IsNullOrEmpty(chunkJson))
                                {
                                    try
                                    {
                                        var docSegment = JsonSerializer.Deserialize<ConsensusDeltaDocument>(chunkJson);
                                        if (docSegment != null && docSegment.DeltaNodes != null)
                                        {
                                            foreach (var node in docSegment.DeltaNodes)
                                            {
                                                await File.WriteAllTextAsync(Path.Combine(deltaDirPath, $"{node.Identity.NodeId}.json"),
                                                    JsonSerializer.Serialize(node));
                                            }
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        _logger.LogError(ex, "[Consensus Coordinator] Failed to parse progressive delta chunk from {Peer}", senderNodeId);
                                    }
                                }

                                if (finished)
                                {
                                    tcs.TrySetResult(true);
                                }
                            },
                            new RouteSettings { Timeout = interChunkTimeout, CancellationToken = cts.Token }
                        );

                        await tcs.Task;
                        _logger.LogInformation("[Consensus Coordinator] Completed progressive delta pull for identifier {Id} from {Peer}", ping.DeltaIdentifier, senderNodeId);
                        await File.WriteAllTextAsync(Path.Combine(_activeSession.Deltas.IncomingDeltasDir, $"{deltaId}.json"), JsonSerializer.Serialize(consensusDeltaFile));

                        lock (_activeSession.Deltas.DeltaLock)
                        {
                            _activeSession.Deltas.DeltasToSync.Add(deltaId);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "[Consensus Coordinator] Progressive delta pull failed targeting {Peer}", senderNodeId);
                    }
                    finally
                    {
                        cts.Dispose();
                        _activeSession.Deltas.ActiveDeltaDownloads.Remove(senderNodeId, out var _);
                    }
                });
            }
            return "ACK";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Consensus Coordinator] Exception in HandleIncomingDeltaRequestAsync processing ping.");
        }
        return "FUCK";
    }

    /// <summary>
    /// Sweeps expired sessions or uncommitted compiled delta file pools to optimize disk storage.
    /// AvailableDeltas holds previously generated deltas with expirations and removes them on timeout.
    /// </summary>
    private void Cleanup()
    {
        UnregisterNegotiationNetworkCommandHandlers();
        UnregisterPeerNetworkCommandHandlers();

        if (_currentMergeIteration != null)
        {
            var op = _currentMergeIteration;
            _currentMergeIteration = null;

            Directory.Delete(op.IterDir, true);
            Directory.Delete(op.MergeDir, true);
        }

        if (_activeSession != null)
        {
            var op = _activeSession;
            _activeSession = null;

            Directory.Delete(op.MergeDeltaDir, true);
            Directory.Delete(op.Deltas.IncomingDeltasDir, true);

            _currentEpochDayKey = string.Empty;
            _consensusStartRequests.Clear();

            op.Control.OrchestrationCts?.Cancel();
            op.Control.NodeMergeCt?.Cancel();
            op.CurrentCSR = null;
            op.MyCSR = null;
            foreach(var value in op.Deltas.DeltaTasks.Values)
            {
                value.Cancel();
            }
            op.Deltas.DeltaTasks.Clear();
        }

        _receivedStartSignal = false;
        _receivedDeltaGossip = false;
        _receivedEndSignal = false;
        _endSessionCts?.Cancel();
    }

    private WeightedConsensusNode? GetWeightedIterationNode(string fromNodeId)
    {
        WeightedConsensusNode? senderNode = null;
        if (_currentMergeIteration != null)
        {
            string senderFilePath = Path.Combine(_currentMergeIteration.IterDir, $"{fromNodeId}.json");
            if (File.Exists(senderFilePath))
            {
                try
                {
                    senderNode = JsonSerializer.Deserialize<WeightedConsensusNode>(File.ReadAllText(senderFilePath));
                }
                catch { }
            }
        }

        if (senderNode != null) return senderNode;

        if (_currentMergeIteration != null)
        {
            string senderFilePath = Path.Combine(_currentMergeIteration.MergeDir, $"{fromNodeId}.json");
            if (File.Exists(senderFilePath))
            {
                try
                {
                    senderNode = JsonSerializer.Deserialize<WeightedConsensusNode>(File.ReadAllText(senderFilePath));
                }
                catch { }
            }
        }

        if (senderNode != null) return senderNode;

        // Fallback to PeerTracker if the sender is not present in our MergeDir record set
        var trackerPeer = _peerTracker.GetPeer(fromNodeId);
        if (trackerPeer != null && trackerPeer.LastSeen.Date == DateTime.UtcNow.Date)
        {
            double smoothing = _ratingSettings.CurrentValue.SenioritySmoothingCoefficient;
            if (smoothing <= 0) smoothing = 7.0;

            double seniority = _ratingService.GetNodeHistoricalSeniority(trackerPeer.Identity.NodeId);
            double historicRating = _ratingService.GetEffectiveTrustScore(trackerPeer.Identity.NodeId, false);

            double shield = seniority > 0 ? seniority / (seniority + smoothing) : 0.0;

            // 3. Enforce strict subjective logic conservation property variables: b + d + u = 1.0
            double uncertainty = Math.Clamp(1.0 - shield, 0.0, 1.0);
            double availableMassPool = 1.0 - uncertainty;

            // 4. Derive underlying node behavior probability from TrustScore metrics scale
            double baseExpectation = Math.Clamp((trackerPeer.TrustScore + 50.0) / 150.0, 0.0, 1.0);

            double belief = 0.0;
            double disbelief = 0.0;

            if (availableMassPool > 0)
            {
                // Distribute total non-uncertain mass across indicators proportionally to the expectation value
                belief = baseExpectation * availableMassPool;
                disbelief = (1.0 - baseExpectation) * availableMassPool;
            }
            else
            {
                // Seniority is zero, total mass collapses purely down into absolute uncertainty
                belief = 0.0;
                disbelief = 0.0;
            }

            var weightedNode = new WeightedConsensusNode
            {
                NodeData = trackerPeer,
                Belief = Math.Clamp(belief, 0.0, 1.0),
                Disbelief = Math.Clamp(disbelief, 0.0, 1.0),
                Uncertainty = uncertainty
            };

            //var localState = new WeightedConsensusNode
            //{
            //    NodeData = trackerPeer,
            //    Belief = 0.5,
            //    Uncertainty = 0.5,
            //    Disbelief = 0.0
            //};

            //double baseExpectation = Math.Clamp((trackerPeer.TrustScore + 50.0) / 150.0, 0.0, 1.0);

            //// Deriving pristine Subjective Logic anchors from the wired-in initial expectation metrics
            //localState.Belief = baseExpectation * 0.8;
            //localState.Disbelief = (1.0 - baseExpectation) * 0.8;
            //localState.Uncertainty = 0.0;

            return weightedNode;
        }

        return null;
    }

    private WeightedConsensusNode MergeNodeRatingBasedOnSender(WeightedConsensusNode localState, PeerNodeTransferred incomingNode, WeightedConsensusNode senderNode)
    {
        double bSender = senderNode.Belief;
        double dSender = senderNode.Disbelief;
        double uSender = senderNode.Uncertainty;

        var localNode = localState.NodeData;

        // --- SUBJECTIVE LOGIC MATH RESOLUTION ---
        // Convert incoming trust score from [-50, 100] to expected probability range [0.0 - 1.0]
        double eIncoming = Math.Clamp((incomingNode.TrustScore + 50.0) / 150.0, 0.0, 1.0);
        double bIncoming = eIncoming * 0.8;
        double dIncoming = (1.0 - eIncoming) * 0.8;
        double uIncoming = 0.2;

        // 1. Recommendation Discounting: Omega_C^{A:B} = Omega_B^A x Omega_C^B
        double bDiscounted = bSender * bIncoming;
        double dDiscounted = bSender * dIncoming;
        double uDiscounted = dSender + uSender + (bSender * uIncoming);

        // 2. Consensus Fusion Merging: Omega_C^merged = Omega_C^A + Omega_C^{A:B}
        double bLocal = localState.Belief;
        double dLocal = localState.Disbelief;
        double uLocal = localState.Uncertainty;

        double divisor = uLocal + uDiscounted - (uLocal * uDiscounted);
        double bMerged = bLocal;
        double dMerged = dLocal;
        double uMerged = uLocal;

        if (divisor > 0.0001)
        {
            bMerged = ((bLocal * uDiscounted) + (bDiscounted * uLocal)) / divisor;
            dMerged = ((dLocal * uDiscounted) + (dDiscounted * uLocal)) / divisor;
            uMerged = (uLocal * uDiscounted) / divisor;
        }

        // 3. Re-map expected value back to local trust score rating
        double eMerged = bMerged + (0.5 * uMerged);
        int mergedTrust = (int)Math.Clamp(Math.Floor(-50.0 + (150.0 * eMerged)), -50, 100);

        localState.Belief = bMerged;
        localState.Disbelief = dMerged;
        localState.Uncertainty = uMerged;
        localState.NodeData.TrustScore = mergedTrust;

        return localState;
    }

    #endregion

    #region ITERATION MERGE METHODS

    /// <summary>
    /// Computes a normalized seniority-weighted confidence modifier protecting established nodes from slow-burn Sybil overtaking.
    /// Bounded strictly in range [0.0 - 1.0].
    /// </summary>

    /// <summary>
    /// File-Oriented Consensus Start. Initializes uncommitted directory structures and stores each baseline node inside individual files.
    /// Strictly separated from session variables.
    /// </summary>
    public async Task StartConsensusMergeAsync()
    {
        string basePath = _p2pSettings.CurrentValue.DataStoragePath ?? "./Module_Data/P2P";
        string baseMergeDir = Path.Combine(basePath, "consensus_merges", "active");

        // ConsensusMergeOperation is SOLELY for merging, it does not care about deltas whatsoever
        _currentMergeIteration = new ConsensusMergeOperation
        {
            IterDir = Path.Combine(baseMergeDir, "ConsensusCurrentIter"),
            MergeDir = Path.Combine(baseMergeDir, "ConsensusCurrentMerge")
        };

        // Create isolated staging environment folder layout
        Directory.CreateDirectory(_currentMergeIteration.IterDir);
        Directory.CreateDirectory(_currentMergeIteration.MergeDir);
        //Directory.CreateDirectory(_currentMergeIteration.NewDocDir);

        _logger.LogInformation("[Consensus Coordinator] Staging folders initialized for active merge iteration.");

        // Extract and transform current cache nodes, combining with local subjective trust weights
        var localNodes = _peerTracker.GetLastSeenNodes(PeerTracker.DefaultNodeComparer).ToList();
        // Fail-safe structural default parameter baseline (7 days to 0.5 saturation)
        double smoothing = _ratingSettings.CurrentValue.SenioritySmoothingCoefficient;
        if (smoothing <= 0) smoothing = 7.0;

        foreach (var peer in localNodes)
        {
            if (peer == null || string.IsNullOrEmpty(peer.Identity.NodeId)) continue;

            double seniority = _ratingService.GetNodeHistoricalSeniority(peer.Identity.NodeId);
            double historicRating = _ratingService.GetEffectiveTrustScore(peer.Identity.NodeId, false);

            double shield = seniority > 0 ? seniority / (seniority + smoothing) : 0.0;

            // 3. Enforce strict subjective logic conservation property variables: b + d + u = 1.0
            double uncertainty = Math.Clamp(1.0 - shield, 0.0, 1.0);
            double availableMassPool = 1.0 - uncertainty;

            // 4. Derive underlying node behavior probability from TrustScore metrics scale
            double baseExpectation = Math.Clamp((peer.TrustScore + 50.0) / 150.0, 0.0, 1.0);

            double belief = 0.0;
            double disbelief = 0.0;

            if (availableMassPool > 0)
            {
                // Distribute total non-uncertain mass across indicators proportionally to the expectation value
                belief = baseExpectation * availableMassPool;
                disbelief = (1.0 - baseExpectation) * availableMassPool;
            }
            else
            {
                // Seniority is zero, total mass collapses purely down into absolute uncertainty
                belief = 0.0;
                disbelief = 0.0;
            }

            var weightedNode = new WeightedConsensusNode
            {
                NodeData = peer,
                Belief = Math.Clamp(belief, 0.0, 1.0),
                Disbelief = Math.Clamp(disbelief, 0.0, 1.0),
                Uncertainty = uncertainty
            };

            string json = JsonSerializer.Serialize(weightedNode, new JsonSerializerOptions { WriteIndented = true });

            // Store each baseline node record inside its own sharded json file to avoid monolithic locks
            await File.WriteAllTextAsync(Path.Combine(_currentMergeIteration.IterDir, $"{peer.Identity.NodeId}.json"), json);
            await File.WriteAllTextAsync(Path.Combine(_currentMergeIteration.MergeDir, $"{peer.Identity.NodeId}.json"), json);
        }
    }

    /// <summary>
    /// Core file-oriented Document swap. Reconstructed directly from iteration staging directories, completely free of session wrappers.
    /// Merges incoming peer node states one by one with uncommitted records inside op.IterDir.
    /// </summary>
    /// 
    public HashSet<string>? StageIncomingNodesAsync(List<PeerNodeTransferred> doc, string fromNodeId)
    {
        if (doc == null || string.IsNullOrEmpty(fromNodeId)) return null;

        var op = _currentMergeIteration;
        if (op == null)
        {
            _logger.LogWarning("[Consensus Coordinator] Rejected incoming consensus document: no active merge iteration found.");
            return null;
        }

        // Load the sender node from the stable baseline in MergeDir to prevent slanders and malicious updates
        WeightedConsensusNode? senderNode = GetWeightedIterationNode(fromNodeId);
        HashSet<string> verifiedNodes = new();

        if (senderNode != null)
        {
            lock (op.IterDir)
            {
                // Process incoming nodes one by one
                foreach (var incomingNode in doc)
                {
                    //get node data from history or whatever
                    WeightedConsensusNode? localState = GetWeightedIterationNode(incomingNode.Identity.NodeId);

                    if (localState != null)
                    {
                        if (!incomingNode.Identity.Signature.SequenceEqual(localState.NodeData.Identity.Signature))
                        {
                            if (!VerifyNode(incomingNode.Identity, localState.NodeData.Identity))
                            {
                                //thefuck?
                                //demote Belief to the incoming node (small)
                                double penaltyRate = 0.05;

                                senderNode.Belief = senderNode.Belief * (1.0 - penaltyRate);
                                senderNode.Disbelief = (senderNode.Disbelief * (1.0 - penaltyRate)) + penaltyRate;

                                // Гарантуємо збереження математичного балансу суми (B + D + U = 1.0)
                                senderNode.Uncertainty = Math.Max(0.0, 1.0 - (senderNode.Belief + senderNode.Disbelief));

                                // 3. Пряме виведення нового TrustScore з оновленого математичного сподівання довіри
                                double currentExpectation = senderNode.Belief + (0.5 * senderNode.Uncertainty);
                                senderNode.NodeData.TrustScore = (int)Math.Clamp(Math.Floor(-50.0 + (150.0 * currentExpectation)), -50, 100);

                                string badSenderNodePath = Path.Combine(op.IterDir, $"{senderNode.NodeData.Identity.NodeId}.json");

                                // Standardized sharded node write back to active IterDir folder
                                File.WriteAllText(badSenderNodePath, JsonSerializer.Serialize(senderNode, new JsonSerializerOptions { WriteIndented = true }));
                                continue;
                            }
                            else
                            {
                                if (incomingNode.Identity.SignatureTime > localState.NodeData.Identity.SignatureTime)
                                {
                                    localState.NodeData.Identity = incomingNode.Identity;
                                }
                            }
                        }

                        localState = MergeNodeRatingBasedOnSender(localState, incomingNode, senderNode);
                        localState.NodeData.LastSeenByNodeId.Add(senderNode.NodeData.Identity.NodeId);

                    }
                    else
                    {
                        //we assume we have a new node
                        if (!VerifyNode(incomingNode.Identity))
                        {
                            //its trash
                            continue;
                        }

                        localState = new WeightedConsensusNode
                        {
                            NodeData = new PeerNodeHistoric(incomingNode),
                            Belief = 0.5,
                            Uncertainty = 0.5,
                            Disbelief = 0.0
                        };

                        double baseExpectation = Math.Clamp((incomingNode.TrustScore + 50.0) / 150.0, 0.0, 1.0);

                        // Deriving pristine Subjective Logic anchors from the wired-in initial expectation metrics
                        double baseBelief = baseExpectation * 0.8;
                        double baseDisbelief = (1.0 - baseExpectation) * 0.8;
                        double baseUncertainty = 0.2;

                        // Extract our subjective opinion boundaries regarding the sender witness
                        double bSender = senderNode.Belief;
                        double dSender = senderNode.Disbelief;
                        double uSender = senderNode.Uncertainty;

                        // Apply standard Subjective Logic recommendation discounting rules
                        localState.Belief = bSender * baseBelief;
                        localState.Disbelief = bSender * baseDisbelief;
                        localState.Uncertainty = dSender + uSender + (bSender * baseUncertainty);

                        // Map the discounted expectation directly back into our relative TrustScore constraints
                        double discountedExpectation = localState.Belief + (0.5 * localState.Uncertainty);
                        localState.NodeData.TrustScore = (int)Math.Clamp(Math.Floor(-50.0 + (150.0 * discountedExpectation)), -50, 100);

                        //recalculate the beliefs
                        localState.NodeData.LastSeenByNodeId.Prepend(senderNode.NodeData.Identity.NodeId);
                    }

                    verifiedNodes.Add(incomingNode.Identity.NodeId);

                    string iterNodePath = Path.Combine(op.IterDir, $"{incomingNode.Identity.NodeId}.json");

                    // Standardized sharded node write back to active IterDir folder
                    string serializedNode = JsonSerializer.Serialize(localState, new JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllText(iterNodePath, serializedNode);
                }
            }
        }

        return verifiedNodes;
    }

    private bool VerifyNode(PeerImmutableData identity, PeerImmutableData? existingIdentity = null)
    {
        if (existingIdentity != null)
        {
            //we do not trust
            if (!(identity.PublicVerifyKey.SequenceEqual(existingIdentity.PublicVerifyKey) &&
                    identity.PublicEncryptionKey.SequenceEqual(existingIdentity.PublicEncryptionKey))) return false;
        }

        return P2PCrypto.TestSignature(
                Convert.ToBase64String(identity.GetNodeDataHash()),
                identity.PublicVerifyKey,
                identity.Signature);
    }

    /// <summary>
    /// Step 3: Merges incoming delta updates directly into the active consensus staging iteration.
    /// Strictly decoupled from session PreviousSyncSet validations.
    /// </summary>
    public void StageIncomingConsensusDeltaAsync(ConsensusDeltaOnDisk delta, string deltaDir, string fromNodeId)
    {
        if (delta == null || string.IsNullOrEmpty(fromNodeId)) return;

        var op = _currentMergeIteration;
        if (op == null)
        {
            _logger.LogWarning("[Consensus Coordinator] Rejected incoming consensus document: no active merge iteration found.");
            return;
        }

        if (_activeSession == null) return;


        // Load the sender node from the stable baseline in MergeDir to prevent slanders and malicious updates
        WeightedConsensusNode? senderNode = GetWeightedIterationNode(fromNodeId);
        HashSet<string> verifiedNodes = new();

        if (senderNode != null)
            lock (op.IterDir)
            { 

                var localMergeFiles = Directory.GetFiles(deltaDir, "*.json", SearchOption.TopDirectoryOnly);

                foreach (var file in localMergeFiles)
                {
                    var incomingNode = JsonSerializer.Deserialize<PeerNodeHistoric>(File.ReadAllText(file));

                    if (incomingNode == null) continue;

                    WeightedConsensusNode? localState = GetWeightedIterationNode(incomingNode.Identity.NodeId);

                    if(localState != null)
                    {
                        if (incomingNode.Identity.Signature != localState.NodeData.Identity.Signature)
                        {
                            if (!VerifyNode(incomingNode.Identity, localState.NodeData.Identity))
                            {
                                //thefuck?
                                //demote Belief to the incoming node (small)
                                double penaltyRate = 0.05;

                                senderNode.Belief = senderNode.Belief * (1.0 - penaltyRate);
                                senderNode.Disbelief = (senderNode.Disbelief * (1.0 - penaltyRate)) + penaltyRate;

                                // Гарантуємо збереження математичного балансу суми (B + D + U = 1.0)
                                senderNode.Uncertainty = Math.Max(0.0, 1.0 - (senderNode.Belief + senderNode.Disbelief));

                                // 3. Пряме виведення нового TrustScore з оновленого математичного сподівання довіри
                                double currentExpectation = senderNode.Belief + (0.5 * senderNode.Uncertainty);
                                senderNode.NodeData.TrustScore = (int)Math.Clamp(Math.Floor(-50.0 + (150.0 * currentExpectation)), -50, 100);

                                string badSenderNodePath = Path.Combine(op.IterDir, $"{senderNode.NodeData.Identity.NodeId}.json");

                                // Standardized sharded node write back to active IterDir folder
                                File.WriteAllText(badSenderNodePath, JsonSerializer.Serialize(senderNode, new JsonSerializerOptions { WriteIndented = true }));
                                continue;
                            }
                            else
                            {
                                if (incomingNode.Identity.SignatureTime > localState.NodeData.Identity.SignatureTime)
                                {
                                    localState.NodeData.Identity = incomingNode.Identity;
                                }
                            }
                        }

                        localState = MergeNodeRatingBasedOnSender(localState, incomingNode, senderNode);
                        localState.NodeData.LastSeenByNodeId.Add(senderNode.NodeData.Identity.NodeId);
                    }
                    else
                    {
                        if (!VerifyNode(incomingNode.Identity))
                        {
                            //its trash
                            continue;
                        }
                        localState = new WeightedConsensusNode
                        {
                            NodeData = new PeerNodeHistoric(incomingNode),
                            Belief = 0.5,
                            Uncertainty = 0.5,
                            Disbelief = 0.0
                        };

                        double baseExpectation = Math.Clamp((incomingNode.TrustScore + 50.0) / 150.0, 0.0, 1.0);

                        // Deriving pristine Subjective Logic anchors from the wired-in initial expectation metrics
                        double baseBelief = baseExpectation * 0.8;
                        double baseDisbelief = (1.0 - baseExpectation) * 0.8;
                        double baseUncertainty = 0.2;

                        // Extract our subjective opinion boundaries regarding the sender witness
                        double bSender = senderNode.Belief;
                        double dSender = senderNode.Disbelief;
                        double uSender = senderNode.Uncertainty;

                        // Apply standard Subjective Logic recommendation discounting rules
                        localState.Belief = bSender * baseBelief;
                        localState.Disbelief = bSender * baseDisbelief;
                        localState.Uncertainty = dSender + uSender + (bSender * baseUncertainty);

                        // Map the discounted expectation directly back into our relative TrustScore constraints
                        double discountedExpectation = localState.Belief + (0.5 * localState.Uncertainty);
                        localState.NodeData.TrustScore = (int)Math.Clamp(Math.Floor(-50.0 + (150.0 * discountedExpectation)), -50, 100);

                        //recalculate the beliefs
                        localState.NodeData.LastSeenByNodeId.Prepend(senderNode.NodeData.Identity.NodeId);

                        if (localState.NodeData.Identity.Roles.Contains("tracker") ||
                            localState.NodeData.Identity.Roles.Contains("promoted_tracker"))
                        {
                            _activeSession.NodesToVisit.Enqueue(senderNode.NodeData.Identity.NodeId);
                        }
                    }

                    string iterNodePath = Path.Combine(op.IterDir, $"{incomingNode.Identity.NodeId}.json");

                    // Standardized sharded node write back to active IterDir folder
                    string serializedNode = JsonSerializer.Serialize(localState, new JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllText(iterNodePath, serializedNode);
                }
            }
    }

    public async Task<string> GenerateIterationDeltaAsync(bool sendRating = false)
    {
        var session = _activeSession;
        if (session == null || session.Operation == null) return string.Empty;

        string mergeIdentif;
        string specificDeltaDir;
        do
        {
            mergeIdentif = GenerateDeltaIdentifierName();
            specificDeltaDir = Path.Combine(session.MergeDeltaDir, mergeIdentif);
        } while (Directory.Exists(specificDeltaDir));

        Directory.CreateDirectory(specificDeltaDir);

        var op = session.Operation;
        List<string> deltaNodes = new List<string>();

        if (Directory.Exists(op.IterDir))
        {
            var iterFiles = Directory.GetFiles(op.IterDir, "*.json");
            foreach (var file in iterFiles)
            {
                string nodeId = Path.GetFileNameWithoutExtension(file);
                string mergeFilePath = Path.Combine(op.MergeDir, $"{nodeId}.json");

                if (!File.Exists(mergeFilePath))
                {
                    deltaNodes.Add(file);
                    continue;
                }
                try
                {
                    var iterNode = JsonSerializer.Deserialize<WeightedConsensusNode>(await File.ReadAllTextAsync(file));
                    var mergeNode = JsonSerializer.Deserialize<WeightedConsensusNode>(await File.ReadAllTextAsync(mergeFilePath));

                    if(iterNode != null && mergeNode != null)
                    {
                        if (Math.Abs(iterNode.NodeData.TrustScore - mergeNode.NodeData.TrustScore) > 10 ||
                            iterNode.NodeData.Identity.SignatureTime > mergeNode.NodeData.Identity.SignatureTime ||
                            iterNode.NodeData.LastSeen > mergeNode.NodeData.LastSeen)
                        {
                            deltaNodes.Add(file);
                            continue;
                        }

                        if(iterNode.NodeData.Identity.SignatureTime > mergeNode.NodeData.Identity.SignatureTime)
                        {
                            deltaNodes.Add(file);
                            continue;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[Consensus Coordinator] Exception compiling delta node for {File}", file);
                }
            }
        }

        if (deltaNodes.Count > 0)
        {
            foreach(var file in deltaNodes)
            {
                File.Copy(file, Path.Combine(specificDeltaDir, Path.GetFileName(file)), overwrite: true);
            }
            session.Deltas.AvailableDeltas[specificDeltaDir] = DateTime.UtcNow.AddMinutes(5); //delta cache = 5 mins
            return mergeIdentif;
        }
        else
        {
            try { Directory.Delete(specificDeltaDir, true); } catch { }
            return string.Empty;
        }
    }

    /// <summary>
    /// Core Iteration Finalization: Promotes staging (ConsensusCurrentIter) files over to 
    /// semi-permanent (ConsensusCurrentMerge) folders.
    /// </summary>
    public void FinalizeConsensusIterationMergeAsync()
    {
        var op = _currentMergeIteration;
        if (op == null) return;

        if (Directory.Exists(op.IterDir))
        {
            var iterFiles = Directory.GetFiles(op.IterDir, "*.json");
            foreach (var file in iterFiles)
            {
                string destFile = Path.Combine(op.MergeDir, Path.GetFileName(file));
                File.Copy(file, destFile, overwrite: true);
                File.Delete(file); // Clear IterDir stage
            }
        }
    }

    /// <summary>
    /// Step 4: Finalizes the entire daily run, writes stable files to ConsensusStore, 
    /// updates indexes, and purges workspace folders.
    /// Processes node records one-by-one to maintain a low memory footprint.
    /// </summary>
    public async Task FinalizeConsensusMergeAsync(string dayKey = "", bool verify = true)
    {
        var op = _currentMergeIteration;
        if (op == null) return;

        // Reset our state pointers to ensure clean termination context
        _currentMergeIteration = null;

        try
        {
            string targetDayKey = !string.IsNullOrEmpty(dayKey) ? dayKey : DateTime.UtcNow.ToString("yyyy-MM-dd");
            _logger.LogInformation("[Consensus Coordinator] Finalizing daily consensus merge snapshot for day {DayKey}", targetDayKey);

            string finalDayDir = Path.Combine(_p2pSettings.CurrentValue.DataStoragePath ?? "./Module_Data/P2P", "history", targetDayKey);
            Directory.CreateDirectory(Path.Combine(finalDayDir, "peers"));
            var manifestBuilder = new StringBuilder();

            // Process nodes one-by-one from the MergeDir directory shards
            var localMergeFiles = Directory.GetFiles(op.MergeDir, "*.json");
            var selfTrust = Math.Clamp(_ratingSettings.CurrentValue.ConsensusSelfTrust, 0, 1);


            foreach (var file in localMergeFiles)
            {
                try
                {
                    string content = await File.ReadAllTextAsync(file);
                    var weightedNode = JsonSerializer.Deserialize<WeightedConsensusNode>(content);

                    if (weightedNode != null && weightedNode.NodeData != null)
                    {
                        var node = weightedNode.NodeData;
                        await _peerTracker.UpdatePeerAsync(node.Identity);

                        var peerExisting = _peerTracker.GetPeer(node.Identity.NodeId, false);

                        if (peerExisting != null)
                        {
                            var peerNew = new PeerNodeRuntime(weightedNode.NodeData) { };
                            peerNew.LastSeenByNodeId.UnionWith(node.LastSeenByNodeId);
                                
                            var peerTrust = _ratingService.GetEffectiveTrustScore(node.Identity.NodeId, false);

                            peerNew.TrustScore = peerTrust * selfTrust + node.TrustScore * (1 - selfTrust);

                            peerNew.LastSeen = DateTime.UtcNow;

                            await _peerTracker.UpdatePeerAsync(peerNew);

                            peerExisting = peerNew;
                        }
                        else
                        {
                            peerExisting = new PeerNodeRuntime(weightedNode.NodeData);
                            await _peerTracker.UpdatePeerAsync(peerExisting);
                        }

                        // Persist to historical store
                        _consensusStore.UpdateHistoricalPeer(targetDayKey, node.Identity.NodeId, node);

                        // Generate manifest entries
                        //fuck is this for?
                        string peerFilePath = Path.Combine(finalDayDir, "peers", $"{node.Identity.NodeId}.json");
                        if (File.Exists(peerFilePath))
                        {
                            byte[] fileBytes = await File.ReadAllBytesAsync(peerFilePath);
                            byte[] fileHash = SHA256.HashData(fileBytes);
                            manifestBuilder.AppendLine($"{node.Identity.NodeId}|{Convert.ToHexString(fileHash)}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[Consensus Coordinator] Failed to process node shard during finalization: {File}", file);
                }
            }

            // Write the historical block sealing manifest
            //useless, i aint gonan use it
            if (manifestBuilder.Length > 0)
            {
                await File.WriteAllTextAsync(Path.Combine(finalDayDir, "manifest.txt"), manifestBuilder.ToString());
            }

            // Cleanup iteration staging folders ONLY (ConsensusMergeOperation folder cleanup responsibility)
            if (Directory.Exists(op.IterDir)) Directory.Delete(op.IterDir, true);
            if (Directory.Exists(op.MergeDir)) Directory.Delete(op.MergeDir, true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Consensus Coordinator] Failed during finalization of consensus merge.");
        }
    }

    public Task AbortConsensusMergeAsync()
    {
        var op = _currentMergeIteration;
        _currentMergeIteration = null;

        if (op != null)
        {
            try
            {
                if (Directory.Exists(op.IterDir)) Directory.Delete(op.IterDir, true);
                if (Directory.Exists(op.MergeDir)) Directory.Delete(op.MergeDir, true);
            }
            catch { }
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Aborts the current uncommitted iteration merge, used on failures. 
    /// Purges uncommitted new documents.
    /// Copies over current pristine state as current iteration.
    /// </summary>
    public Task ResetIterationMergeAsync()
    {
        var op = _currentMergeIteration;
        if (op == null) return Task.CompletedTask;

        _logger.LogWarning("[Consensus Coordinator] Fail-safe active iteration rollback triggered. Purging iteration-specific uncommitted states.");
        try
        {
            if (Directory.Exists(op.IterDir))
            {
                foreach (var file in Directory.GetFiles(op.IterDir))
                {
                    File.Delete(file);
                }
            }

            if (Directory.Exists(op.MergeDir))
            {
                foreach (var file in Directory.GetFiles(op.MergeDir))
                {
                    File.Copy(file, Path.Combine(op.IterDir, Path.GetFileName(file)));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Consensus Coordinator] Exception encountered during active iteration rollback procedure.");
        }
        return Task.CompletedTask;
    }

    #endregion

    public override void Dispose()
    {
        Cleanup();
        _routerService.UnregisterCommandHandler("ConsensusStartSignal");
        _routerService.UnregisterCommandHandler("RequestConsensusLatestSegmented");

        base.Dispose();
    }
}*/