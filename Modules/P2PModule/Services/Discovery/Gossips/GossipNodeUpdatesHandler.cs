using System.Collections.Concurrent;
using System.Text.Json;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using P2PModule.Configuration;
using P2PModule.Infrastructure;
using P2PModule.Interfaces;
using P2PModule.Models;
using P2PModule.Models.Gossips;
using P2PModule.Services.Persistence;
using P2PModule.Services.Rating;

namespace P2PModule.Services.Discovery.Gossip;

/// <summary>
/// Manages topic-based gossip message propagation with a single unified, thread-safe priority queue.
/// Leverages the outer P2PPacketEnvelope.Ttl metadata for dynamic hop decay,
/// protecting the signed GossipMessage payload from mutation-induced signature breakage.
/// </summary>
public class GossipNodeUpdatesHandler : IHostedService, IDisposable
{
    private readonly IConsensusStore _archiveStore;
    private readonly IPeerTracker _peerTracker;
    private readonly IP2PGossipService _gossipService;
    private readonly INodeIdentityService _identity;
    private readonly IRatingService _ratingService;
    private readonly ILogger<GossipNodeUpdatesHandler> _logger;

    //new gossiped nodes
    private readonly HashSet<string> _nodesToGossip = new();
    private readonly HashSet<string> _absolutelyNewNodes = new();
    private readonly ConcurrentDictionary<string, int> _absolutelyNewNodeTrackers = new();

    private readonly IOptionsMonitor<GossipSettings> _gossipSetting;
    private readonly IOptionsMonitor<StorageSettings> _storageSettings;

    private int _gossipBatches;
    private int _newNodeGossipLimit;

    private Timer? _throttledFlushTimer;
    private IDisposable? _settingsChangeListener;

    public GossipNodeUpdatesHandler(
        IConsensusStore archiveStore,
        IPeerTracker peerTracker,
        IP2PGossipService gossipService,
        INodeIdentityService identity,
        IRatingService ratingService,
        IOptionsMonitor<GossipSettings> gossipSetting,
        IOptionsMonitor<StorageSettings> settings,
        ILogger<GossipNodeUpdatesHandler> logger)
    {
        _peerTracker = peerTracker;
        _archiveStore = archiveStore;
        _ratingService = ratingService;
        _gossipService = gossipService;
        _identity = identity;
        _identity = identity;
        _logger = logger;

        _gossipSetting = gossipSetting;
        _storageSettings = settings;

        //_newNodeGossipLimit = _gossipSetting.CurrentValue.MaxNewNodesDrip;
        //_gossipBatches = _gossipSetting.CurrentValue.GossipInBatches;
        //_settingsChangeListener = _gossipSetting.OnChange((settings, name) => UpdateSettings());
        //UpdateSettings();
    }
    private void UpdateSettings()
    {
        _gossipBatches = _gossipSetting.CurrentValue.GossipInBatches;
        _newNodeGossipLimit = _gossipSetting.CurrentValue.MaxNewNodesDrip;

        var timespan = TimeSpan.FromSeconds(_gossipSetting.CurrentValue.GossipNodesSeconds);
        _throttledFlushTimer?.Change(timespan, timespan);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _gossipBatches = _gossipSetting.CurrentValue.GossipInBatches;
        _newNodeGossipLimit = _gossipSetting.CurrentValue.MaxNewNodesDrip;

        _peerTracker.OnPeerNew += peer_OnNewPeer;
        _peerTracker.OnPeerUpdate += peer_OnPeerUpdate;

        _gossipService.Subscribe("NodeUpdates", NodeUpdatesReceived);

        // Schedule explicit disk dump serialization loops every 5 minutes
        _throttledFlushTimer = new Timer(async _
            => await BroadcastPendingNodeUpdatesAsync(), null,
            TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));

        _settingsChangeListener = _gossipSetting.OnChange((settings, name) => UpdateSettings());

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _settingsChangeListener?.Dispose();
        _throttledFlushTimer?.Dispose();
        _peerTracker.OnPeerNew -= peer_OnNewPeer;
        _peerTracker.OnPeerUpdate -= peer_OnNewPeer;
        return Task.CompletedTask;
    }


    /// <summary>
    /// Ingests incoming node gossip messages. Requires at least one signature to verify before accepting.
    /// </summary>
    private async Task NodeUpdatesReceived(GossipMessage message, string fromNodeId)
    {
        if (message == null || string.IsNullOrEmpty(message.SerializedPayload)) return;

        try
        {
            var incomingNodes = JsonSerializer.Deserialize<NodeUpdateGossip>(message.SerializedPayload);
            if (incomingNodes == null || incomingNodes.Nodes == null || incomingNodes.Nodes.Count == 0) return;

            int newNodesFromThisTrackerCount = 0;

            foreach (var incomingNode in incomingNodes.Nodes)
            {
                if (incomingNode?.Identity == null || string.IsNullOrEmpty(incomingNode.Identity.NodeId))
                    continue;

                string targetNodeId = incomingNode.Identity.NodeId;

                // Rule: Must pass lineage verification (direct neighbour OR zero failures on historical signatures)
                if (!VerifyAtLeastOneSignature(incomingNode, targetNodeId, fromNodeId))
                {
                    _logger.LogWarning("[Sybil Guard] Dropping gossip for {TargetNodeId} from {FromNodeId} - Lineage verification failed.", targetNodeId, fromNodeId);
                    continue;
                }

                // Prevent future-dated timestamps
                DateTime nowUtc = DateTime.UtcNow;
                if (incomingNode.Identity.SignatureTime > nowUtc.AddMinutes(5))
                {
                    _logger.LogWarning("[Gossip Engine] Dropping node update for {TargetNodeId} - Signature time is in the future.", targetNodeId);
                    continue;
                }

                bool isNewNode = !_peerTracker.IsPeerExists(targetNodeId);

                if (isNewNode)
                {
                    newNodesFromThisTrackerCount++;
                    var node = new PeerNodeRuntime
                    {
                        Identity = incomingNode.Identity,
                        LastSeen = nowUtc,
                        TrustScore = 0,
                        IsVerified = (targetNodeId == fromNodeId),
                        Signatures = incomingNode.Signatures ?? new List<SignatureEnvelope>()
                    };

                    await _peerTracker.UpdatePeerAsync(node);
                }
                else
                {
                    var oldNode = _peerTracker.GetPeer(targetNodeId);

                    // Update stored metadata only if incoming identity signature time is strictly newer
                    if (oldNode != null && (oldNode.Identity == null || incomingNode.Identity.SignatureTime > oldNode.Identity.SignatureTime))
                    {
                        if (oldNode.IsVerified && (targetNodeId != fromNodeId))
                        {
                            _logger.LogWarning("[Sybil Guard] Prevented unverified gossip overwrite for verified node {TargetNodeId}.", targetNodeId);
                            continue;
                        }

                        oldNode.Identity = incomingNode.Identity;
                        oldNode.LastSeen = nowUtc;
                        oldNode.Signatures = incomingNode.Signatures ?? new List<SignatureEnvelope>();
                        if (targetNodeId == fromNodeId) oldNode.IsVerified = true;

                        await _peerTracker.UpdatePeerAsync(oldNode);
                    }

                    await _peerTracker.UpdatePeerWasSeenByAsync(targetNodeId, fromNodeId);
                }
            }

            // Rate-limit fresh node injections to mitigate Sybil flood attacks
            if (newNodesFromThisTrackerCount > 0)
            {
                _absolutelyNewNodeTrackers.AddOrUpdate(
                    fromNodeId,
                    newNodesFromThisTrackerCount,
                    (key, existingCount) => existingCount + newNodesFromThisTrackerCount
                );

                if (_absolutelyNewNodeTrackers.TryGetValue(fromNodeId, out int totalDumpedCount))
                {
                    int maxAllowedFresh = _newNodeGossipLimit;
                    if (totalDumpedCount > maxAllowedFresh)
                    {
                        int overage = totalDumpedCount - maxAllowedFresh;
                        int finalDemotionPenalty = Math.Min(newNodesFromThisTrackerCount, overage) * _gossipSetting.CurrentValue.NodeDripPenalty;

                        _logger.LogWarning("[Sybil Guard] Tracker {FromNodeId} exceeded fresh identity limit. Enforcing trust penalty: -{Penalty}.",
                            fromNodeId, finalDemotionPenalty);

                        await _ratingService.ApplyLocalReputationUpdateAsync(message.OriginNodeId, -finalDemotionPenalty);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Peer Tracker Engine] Handling incoming 'NodeUpdates' topic payload failed.");
        }
    }

    /// <summary>
    /// Verifies if at least one signature passes verification (either Identity self-signature or a historical handshake envelope).
    /// </summary>
    private bool VerifyAtLeastOneSignature(NodeUpdateData incomingNode, string targetNodeId, string fromNodeId)
    {
        byte[] nodeHash = incomingNode.Identity.GetNodeDataHash();

        // 1. Check Identity self-signature
        bool isSelfSigValid = incomingNode.Identity.PublicVerifyKey != null &&
                              incomingNode.Identity.PublicVerifyKey.Length > 0 &&
                              incomingNode.Identity.Signature != null &&
                              incomingNode.Identity.Signature.Length > 0 &&
                              P2PCrypto.TestSignature(nodeHash, incomingNode.Identity.PublicVerifyKey, incomingNode.Identity.Signature);

        if (!isSelfSigValid) return false;

        // Direct neighbor sending its own update -> Verified
        if (targetNodeId == fromNodeId || !_peerTracker.IsPeerExists(targetNodeId))
        {
            return true;
        }

        // 2. Unverified Third-Party Gossip: Perform full historical lineage sweep
        if (incomingNode.Signatures == null || incomingNode.Signatures.Count == 0)
        {
            return false;
        }

        int verifiedEnvelopesCount = 0;

        foreach (var envelope in incomingNode.Signatures)
        {
            if (envelope == null || string.IsNullOrEmpty(envelope.DayKey) || envelope.Signature == null || envelope.Signature.Length == 0)
                continue;

            var histPeer = _archiveStore.GetHistoricalPeer(envelope.DayKey, targetNodeId);
            if (histPeer?.Identity?.PublicVerifyKey == null || histPeer.Identity.PublicVerifyKey.Length > 0)
                continue;

            byte[] signedPayload;
            if (envelope.Nonce != null && envelope.Nonce.Length > 0)
            {
                signedPayload = new byte[envelope.Nonce.Length + nodeHash.Length];
                Buffer.BlockCopy(envelope.Nonce, 0, signedPayload, 0, envelope.Nonce.Length);
                Buffer.BlockCopy(nodeHash, 0, signedPayload, envelope.Nonce.Length, nodeHash.Length);
            }
            else
            {
                signedPayload = nodeHash;
            }

            // Zero failures allowed for days present in local store
            if (!P2PCrypto.TestSignature(signedPayload, histPeer.Identity.PublicVerifyKey, envelope.Signature))
            {
                return false;
            }

            verifiedEnvelopesCount++;
        }

        return verifiedEnvelopesCount > 0;
    }

    /// <summary>
    /// Flushes enqueued node updates using throttled batching.
    /// </summary>
    private async Task BroadcastPendingNodeUpdatesAsync()
    {
        try
        {
            int batchSize = _gossipBatches <= 0 ? _newNodeGossipLimit : _gossipBatches;
            int newNodesLimitRemaining = _newNodeGossipLimit;
            var allowedNodeIds = new List<string>();

            lock (_nodesToGossip)
            {
                if (_nodesToGossip.Count == 0) return;

                var queuedNodeIds = _nodesToGossip.ToList();
                _nodesToGossip.Clear();

                foreach (var id in queuedNodeIds)
                {
                    bool isNewNode = _absolutelyNewNodes.Contains(id);
                    if (isNewNode)
                    {
                        if (newNodesLimitRemaining > 0)
                        {
                            allowedNodeIds.Add(id);
                            _absolutelyNewNodes.Remove(id);
                            newNodesLimitRemaining--;
                        }
                        else
                        {
                            _nodesToGossip.Add(id);
                        }
                    }
                    else
                    {
                        allowedNodeIds.Add(id);
                    }
                }
            }

            while (allowedNodeIds.Count > 0)
            {
                var batchNodeIds = allowedNodeIds.Take(batchSize).ToList();
                allowedNodeIds.RemoveRange(0, batchNodeIds.Count);

                var fullGossip = new NodeUpdateGossip();

                foreach (var id in batchNodeIds)
                {
                    var node = _peerTracker.GetPeer(id, false);
                    if (node != null && node.Identity != null)
                    {
                        var gossipItem = new NodeUpdateData
                        {
                            Identity = node.Identity,
                            Signatures = node.Signatures ?? new List<SignatureEnvelope>()
                        };
                        fullGossip.Nodes.Add(gossipItem);
                    }
                }

                if (fullGossip.Nodes.Count == 0) continue;

                string payload = JsonSerializer.Serialize(fullGossip);
                await _gossipService.PublishAsync("NodeUpdate", payload, 5);
            }

            _absolutelyNewNodeTrackers.Clear();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Peer Tracker Engine] Throttled serialization or gossip broadcast failed.");
        }
    }

    private void peer_OnPeerUpdate(PeerNodeRuntime node)
    {
        if (node?.Identity?.NodeId != null)
        {
            lock (_nodesToGossip)
            {
                _nodesToGossip.Add(node.Identity.NodeId);
            }
        }
    }

    private void peer_OnNewPeer(PeerNodeRuntime node)
    {
        if (node?.Identity?.NodeId != null)
        {
            lock (_nodesToGossip)
            {
                _nodesToGossip.Add(node.Identity.NodeId);
                _absolutelyNewNodes.Add(node.Identity.NodeId);
            }
        }
    }

    public void Dispose()
    {
        _settingsChangeListener?.Dispose();
        _throttledFlushTimer?.Dispose();

        if (_peerTracker != null)
        {
            _peerTracker.OnPeerNew -= peer_OnNewPeer;
            _peerTracker.OnPeerUpdate -= peer_OnPeerUpdate;
        }
    }

}