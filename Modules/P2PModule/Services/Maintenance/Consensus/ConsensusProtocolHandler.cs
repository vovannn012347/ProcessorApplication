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

    public byte[] GetHash()
    {
        return SHA256.HashData(
            Encoding.UTF8.GetBytes($"{NodeIdFrom}|{TimeStamp:o}|{NonTransitive}|{Nonce}"));
    }
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
    private readonly ILogger<ConsensusProtocolHandler> _logger;

    private CancellationTokenSource? _sessionLoopCts;
    private const int NodeSwapBatchSize = 50;
    private const int DeltaNodeBatchSize = 40;
    private const int RetryDelayMs = 1500;

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
        ILogger<ConsensusProtocolHandler> logger)
    {
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

    public async Task<bool> TriggerConsensusAsync(CancellationToken cancellationToken = default)
    {
        if (_sessionManager.IsBusy())
        {
            _logger.LogWarning("[Consensus Protocol] Cannot trigger manual consensus - session is already busy.");
            return false;
        }

        if (!_sessionManager.TryAcquireSession(_identity.NodeId, out var session) || session == null)
        {
            return false;
        }

        SeedVisitQueueFromPeerTracker();
        RegisterConsensusNetworkHandlers();

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
                    await CompleteAndFinalizeSessionAsync();
                    break;
                }

                while ((_sessionManager.TryPopNextNodeToVisit(out var candidateNodeId) || !string.IsNullOrEmpty(session.TargetNodeId)) && !ct.IsCancellationRequested)
                {
                    string? targetNodeId = !string.IsNullOrEmpty(session.TargetNodeId) ? session.TargetNodeId : candidateNodeId;
                    if (string.IsNullOrEmpty(targetNodeId)) continue;

                    bool connected = await EnsurePeerConnectionAsync(targetNodeId);
                    if (!connected)
                    {
                        _logger.LogWarning("[Consensus Protocol] Connection to {TargetNodeId} failed. Marking node visited.", targetNodeId);
                        _sessionManager.MarkNodeAsVisited(targetNodeId);
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
                            _logger.LogInformation("[Consensus Protocol] Target {TargetNodeId} start ping returned '{Status}'. Skipping for now.", targetNodeId, startStatus);
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
                        else if (string.Equals(initStatus, "ALREADY_SYNCED", StringComparison.OrdinalIgnoreCase))
                        {
                            _logger.LogInformation("[Consensus Protocol] Target {TargetNodeId} reports ALREADY_SYNCED.", targetNodeId);
                            _sessionManager.MarkNodeAsVisited(targetNodeId);
                            continue;
                        }
                        else if (initStatus.StartsWith("BUSY", StringComparison.OrdinalIgnoreCase) ||
                                 initStatus.StartsWith("WTF_BUSY", StringComparison.OrdinalIgnoreCase))
                        {
                            _logger.LogInformation("[Consensus Protocol] Target {TargetNodeId} is BUSY ({Status}). Re-queuing with retry backoff.", targetNodeId, initStatus);
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
                    if (swapOk)
                    {
                        await RequestAndAttachSyncSetSignatureAsync(targetNodeId, session, ct);
                    }

                    _sessionManager.SetActiveTargetNode(null);

                    // Step 3: Ingest pending deltas, generate local delta, finalize iteration
                    await IngestPendingDeltasAsync(session);
                    await CreateAndBroadcastLocalDeltaAsync(session, ct);
                    await _sessionManager.FinalizeIterationStepAsync();

                    await Task.Delay(250, ct);
                }

                _logger.LogInformation("[Consensus Protocol] Visit queue empty. Entering post-expansion idling state.");
                await SyncMergeDirToConsensusStoreAsync(session);

                while (_sessionManager.IsBusy() && !ct.IsCancellationRequested)
                {
                    if (IsMidnightBoundaryReached())
                    {
                        _logger.LogWarning("[Consensus Protocol] Midnight UTC boundary reached during idling phase. Finalizing session.");
                        await CompleteAndFinalizeSessionAsync();
                        return;
                    }

                    if (_sessionManager.HasPendingDeltas())
                    {
                        _logger.LogInformation("[Consensus Protocol] Pending deltas detected during idling. Ingesting...");
                        await IngestPendingDeltasAsync(session);
                        await CreateAndBroadcastLocalDeltaAsync(session, ct);
                        await _sessionManager.FinalizeIterationStepAsync();
                        await SyncMergeDirToConsensusStoreAsync(session);
                    }

                    if (DateTime.UtcNow - session.LastActivityUtc >= TimeSpan.FromMinutes(5))
                    {
                        _logger.LogInformation("[Consensus Protocol] Inactivity threshold (5m) reached. Finalizing consensus.");
                        await BroadcastConsensusFinishedAsync(session);
                        await CompleteAndFinalizeSessionAsync();
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
            await CompleteAndFinalizeSessionAsync();
        }
    }


    private async Task<string> HandleIncomingStartPingAsync(string senderNodeId, P2PPacketEnvelope frame, CancellationToken ct)
    {
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

        _logger.LogInformation("[Consensus Protocol] Received ConsensusStartPing from {SenderNodeId}. Debuffing rating and starting session...", senderNodeId);
        await _ratingService.ApplyLocalReputationUpdateAsync(senderNodeId, -5);

        if (_sessionManager.TryAcquireSession(_identity.NodeId, out var newSession) && newSession != null)
        {
            SeedVisitQueueFromPeerTracker();
            RegisterConsensusNetworkHandlers();

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
            var myReq = new ConsensusRequest
            {
                NodeIdFrom = _identity.NodeId,
                TimeStamp = DateTime.UtcNow,
                NonTransitive = true,
                Nonce = Guid.NewGuid().ToString("N")
            };
            myReq.Signature = P2PCrypto.SignData(myReq.GetHash(), _identity.PrivateSignatureKey);

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
            var myReq = new ConsensusRequest
            {
                NodeIdFrom = _identity.NodeId,
                TimeStamp = DateTime.UtcNow,
                NonTransitive = true,
                Nonce = Guid.NewGuid().ToString("N")
            };
            myReq.Signature = P2PCrypto.SignData(myReq.GetHash(), _identity.PrivateSignatureKey);

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

        if (!string.IsNullOrEmpty(frame.SerializedPayload))
        {
            try
            {
                var req = JsonSerializer.Deserialize<ConsensusRequest>(frame.SerializedPayload);
                if (req == null)
                {
                    await _ratingService.ApplyLocalReputationUpdateAsync(senderNodeId, -5);
                    return "FUCK_THIS_REQUEST";
                }

                if (Math.Abs((DateTime.UtcNow - req.TimeStamp).TotalMinutes) > 5.0)
                {
                    _logger.LogWarning("[Anti-Tamper Guard] Rejected stale init request from {SenderNodeId}.", senderNodeId);
                    await _ratingService.ApplyLocalReputationUpdateAsync(senderNodeId, -10);
                    return "FUCK_TIMESTAMP";
                }

                var senderPeer = _peerTracker.GetPeer(senderNodeId);
                if (senderPeer?.Identity?.PublicVerifyKey != null && senderPeer.Identity.PublicVerifyKey.Length > 0)
                {
                    if (!P2PCrypto.TestSignature(req.GetHash(), senderPeer.Identity.PublicVerifyKey, req.Signature))
                    {
                        _logger.LogWarning("[Anti-Tamper Guard] Signature verification failed on init request from {SenderNodeId}.", senderNodeId);
                        await _ratingService.ApplyLocalReputationUpdateAsync(senderNodeId, -15);
                        return "FUCK_YOU_ARE";
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Consensus Protocol] Error deserializing ConsensusRequest payload from {SenderNodeId}.", senderNodeId);
                return "FUCK_THIS_REQUEST";
            }
        }

        if (!string.IsNullOrEmpty(session.TargetNodeId) && !string.Equals(session.TargetNodeId, senderNodeId, StringComparison.OrdinalIgnoreCase))
        {
            return $"WTF_BUSY|{session.TargetNodeId}";
        }

        if (session.PastSyncSet.Contains(senderNodeId) || session.VisitedNodes.Contains(senderNodeId))
        {
            return "ALREADY_SYNCED";
        }

        _sessionManager.SetActiveTargetNode(senderNodeId);
        _logger.LogInformation("[Consensus Protocol] Accepted lock-in init request from {SenderNodeId}.", senderNodeId);

        return "OK_AVAILABLE";
    }


    private async Task<bool> ExecutePairwiseSwapAsync(string targetNodeId, ConsensusMergeSession session, CancellationToken ct)
    {
        try
        {
            bool hasReceivedData = false;
            var tcs = new TaskCompletionSource<bool>();

            await _routerService.SendRequestProgressiveAsync(
                targetNodeId,
                "ConsensusSwap",
                string.Empty,
                (payloadJson, error, progress, isCompleted) =>
                {
                    if (!string.IsNullOrEmpty(error))
                    {
                        _logger.LogWarning("[Consensus Protocol] Progressive swap segment error from {Target}: {Error}", targetNodeId, error);
                        if (isCompleted) tcs.TrySetResult(hasReceivedData);
                        return;
                    }

                    if (!string.IsNullOrEmpty(payloadJson))
                    {
                        try
                        {
                            var chunk = JsonSerializer.Deserialize<ConsensusNodeSwapPayload>(payloadJson);
                            if (chunk?.Nodes != null && chunk.Nodes.Count > 0)
                            {
                                hasReceivedData = true;
                                _ = Task.Run(async () =>
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

                                    _sessionManager.RegisterNodesInCurrentSyncSet(nodeIds);
                                    _sessionManager.QueueNodesToVisit(nodeIds);
                                }, ct);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "[Consensus Protocol] Error processing incoming node swap chunk from {TargetNodeId}.", targetNodeId);
                        }
                    }

                    if (isCompleted)
                    {
                        tcs.TrySetResult(hasReceivedData);
                    }
                },
                new RouteSettings { Timeout = TimeSpan.FromSeconds(20), CancellationToken = ct }
            );

            return await tcs.Task;
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
            var prevSig = fullChain.LastOrDefault();
            int order = fullChain.Count + 1;
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
                    _sessionManager.AddSyncSetSignature(targetNodeId, _identity.NodeId, responseSig.Signature, responseSig.Order);
                }
            }

            _sessionManager.AddSyncSetSignature(_identity.NodeId, targetNodeId, localSigBytes, order);

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

        var localNodes = await LoadLocalNodesForSwapAsync(session);
        int totalNodes = localNodes.Count;

        if (totalNodes == 0)
        {
            var emptyPayload = new ConsensusNodeSwapPayload();
            await _routerService.SendProgressiveResponseAsync(
                senderNodeId, frame.MessageId, "ConsensusSwap", JsonSerializer.Serialize(emptyPayload), 100, true);
            return null!;
        }

        for (int i = 0; i < totalNodes; i += NodeSwapBatchSize)
        {
            var batch = localNodes.Skip(i).Take(NodeSwapBatchSize).ToList();
            var payload = new ConsensusNodeSwapPayload { Nodes = batch };

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
                _sessionManager.RegisterNodesInCurrentSyncSet(req.ExchangedNodeIds);
                foreach (var nodeId in req.ExchangedNodeIds)
                {
                    await _peerTracker.UpdatePeerWasSeenByAsync(nodeId, senderNodeId);
                }
            }

            if (req.FullSignatureChain != null && req.FullSignatureChain.Count > 0)
            {
                foreach (var sig in req.FullSignatureChain)
                {
                    _sessionManager.AddSyncSetSignature(sig.SignerNodeId, sig.TargetNodeId, sig.Signature, sig.Order);
                }
            }

            int order = req.SignatureOrder + 1;
            long timestamp = DateTime.UtcNow.Ticks;
            byte[] targetSigBytes = ComputeChainLinkSignature(
                req.PreviousSignature?.Signature, order, _identity.NodeId, senderNodeId, timestamp, session.DayKeySignatureKey);

            var returnSignature = new ChainSignature
            {
                Order = order,
                SignerNodeId = _identity.NodeId,
                TargetNodeId = senderNodeId,
                TimestampUtc = new DateTime(timestamp, DateTimeKind.Utc),
                Signature = targetSigBytes
            };

            _sessionManager.AddSyncSetSignature(_identity.NodeId, senderNodeId, targetSigBytes, order);

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

            if (session.LocalDeltas.ContainsKey(remoteDeltaId) || session.PendingDeltasToMerge.ContainsKey(remoteDeltaId))
                return "ALREADY_SYNCED";

            if (_sessionManager.PrepareDownloadedDeltaFolder(out string assignedDeltaId, out string destFolderPath))
            {
                bool downloaded = await DownloadDeltaFilesAsync(senderNodeId, remoteDeltaId, destFolderPath, ct);
                if (downloaded)
                {
                    bool registered = _sessionManager.RegisterDownloadedDelta(senderNodeId, destFolderPath, ping.SignatureChain);
                    if (registered)
                    {
                        _logger.LogInformation("[Consensus Protocol] Successfully downloaded and registered delta {DeltaId} from {Sender}.", assignedDeltaId, senderNodeId);
                    }
                    else
                    {
                        _logger.LogWarning("[Anti-Tamper Guard] Downloaded delta {DeltaId} from {Sender} failed signature validation. Applying reputation penalty.", assignedDeltaId, senderNodeId);
                        await _ratingService.ApplyLocalReputationUpdateAsync(senderNodeId, -15);
                    }
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

                            // Stage parsed nodes immediately
                            var activeSession = _sessionManager.GetActiveSession();
                            if (activeSession?.StagingOperation != null && transferredList.Count > 0)
                            {
                                _ = _mergeEngine.StageIncomingNodeSetAsync(activeSession.StagingOperation, senderNodeId, transferredList);
                            }
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
                    try
                    {
                        var weighted = JsonSerializer.Deserialize<WeightedConsensusNode>(json);
                        node = weighted?.NodeData;
                    }
                    catch
                    {
                        node = JsonSerializer.Deserialize<PeerNodeHistoric>(json);
                    }

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

        var pastNodes = _sessionManager.GetPastSyncSetNodes();
        if (pastNodes.Count == 0) return;

        var ping = new ConsensusDeltaPing
        {
            DeltaFromNode = _identity.NodeId,
            DeltaIdentifier = deltaItem.DeltaId,
            DeltaVerifyKey = session.DayKeyVerifyKey,
            SignatureChain = _sessionManager.GetSyncSetSignatures(),
            SourceSignatureChain = _sessionManager.GetSyncSetSignatures(),
            Nodes = new HashSet<string>(session.CurrentSyncSet)
        };

        string payload = JsonSerializer.Serialize(ping);

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
            try
            {
                string deltaNodesDir = Path.Combine(delta.DeltaFolderPath, "nodes");
                if (Directory.Exists(deltaNodesDir))
                {
                    foreach (var file in Directory.GetFiles(deltaNodesDir, "*.json"))
                    {
                        string json = await File.ReadAllTextAsync(file);
                        PeerNodeHistoric? peerHistoric = null;
                        try
                        {
                            var weighted = JsonSerializer.Deserialize<WeightedConsensusNode>(json);
                            peerHistoric = weighted?.NodeData;
                        }
                        catch
                        {
                            peerHistoric = JsonSerializer.Deserialize<PeerNodeHistoric>(json);
                        }

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
        if (!Directory.Exists(mergeDir)) return;

        try
        {
            foreach (var file in Directory.GetFiles(mergeDir, "*.json"))
            {
                string nodeId = Path.GetFileNameWithoutExtension(file);
                string json = await File.ReadAllTextAsync(file);
                PeerNodeHistoric? historicPeer = null;
                try
                {
                    var weighted = JsonSerializer.Deserialize<WeightedConsensusNode>(json);
                    historicPeer = weighted?.NodeData;
                }
                catch
                {
                    historicPeer = JsonSerializer.Deserialize<PeerNodeHistoric>(json);
                }

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
            .Where(p => p.Identity!.Roles.Contains("tracker") || p.Identity.Roles.Contains("promoted_tracker"))
            .Select(p => p.Identity!.NodeId)
            .ToList();

        if (trackerCandidates.Count > 0)
        {
            _ = EnsureSelfDemotedAsync();
            _sessionManager.QueueNodesToVisit(trackerCandidates);
            _logger.LogInformation("[Consensus Protocol] Seeded visit queue with {Count} tracker/promoted_tracker nodes.", trackerCandidates.Count);
        }
        else
        {
            _logger.LogWarning("[Consensus Role] No trackers found in vicinity! Promoting local node to 'promoted_tracker' to drive fallback loop.");
            _ = PromoteSelfToTrackerAsync();

            var peerCandidates = allPeers.Select(p => p.Identity!.NodeId).ToList();
            _sessionManager.QueueNodesToVisit(peerCandidates);
        }
    }

    private async Task PromoteSelfToTrackerAsync()
    {
        var capacities = _identity.GetCapablities();
        if (!capacities.Contains("promoted_tracker"))
        {
            capacities.Add("promoted_tracker");
            var roleName = nameof(_p2pSettings.CurrentValue.NodeRoles);

            using (var scope = _scopeFactory.CreateScope())
            {
                var settingService = scope.ServiceProvider.GetRequiredService<ISettingService>();
                await settingService.SetAsync(P2PModuleEntryPoint.MODULE_ID, $"{nameof(P2PSettings)}:{roleName}", capacities);
            }

            _identity.TriggerUpdate();
            _logger.LogInformation("[Consensus Role] Local node successfully promoted to 'promoted_tracker'.");
        }
    }

    private async Task EnsureSelfDemotedAsync()
    {
        var capacities = _identity.GetCapablities();
        if (capacities.Contains("promoted_tracker"))
        {
            capacities.Remove("promoted_tracker");
            var roleName = nameof(_p2pSettings.CurrentValue.NodeRoles);

            using (var scope = _scopeFactory.CreateScope())
            {
                var settingService = scope.ServiceProvider.GetRequiredService<ISettingService>();
                await settingService.SetAsync(P2PModuleEntryPoint.MODULE_ID, $"{nameof(P2PSettings)}:{roleName}", capacities);
            }

            _identity.TriggerUpdate();
            _logger.LogInformation("[Consensus Role] Official trackers detected. Demoted local node from 'promoted_tracker'.");
        }
    }

    private async Task<List<PeerNodeTransferred>> LoadLocalNodesForSwapAsync(ConsensusMergeSession session)
    {
        var list = new List<PeerNodeTransferred>();
        if (session.StagingOperation == null) return list;

        string mergeDir = session.StagingOperation.MergeDir;
        if (!Directory.Exists(mergeDir)) return list;

        foreach (var file in Directory.GetFiles(mergeDir, "*.json"))
        {
            try
            {
                string json = await File.ReadAllTextAsync(file);
                PeerNodeHistoric? historic = null;
                try
                {
                    var weighted = JsonSerializer.Deserialize<WeightedConsensusNode>(json);
                    historic = weighted?.NodeData;
                }
                catch
                {
                    historic = JsonSerializer.Deserialize<PeerNodeHistoric>(json);
                }

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
    }

    private void UnregisterConsensusNetworkHandlers()
    {
        _routerService.UnregisterCommandHandler("ConsensusMergeInitRequest");
        _routerService.UnregisterCommandHandler("ConsensusSwap");
        _routerService.UnregisterCommandHandler("ConsensusSignatureRequest");
        _routerService.UnregisterCommandHandler("ConsensusDeltaPing");
        _routerService.UnregisterCommandHandler("ConsensusDeltaDownload");
        _routerService.UnregisterCommandHandler("ConsensusFinished");
    }

    private async Task CompleteAndFinalizeSessionAsync()
    {
        UnregisterConsensusNetworkHandlers();
        await _sessionManager.FinalizeSessionAsync();
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