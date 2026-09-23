// [full-implementation]
using System.Text.Json;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using P2PModule.Configuration;
using P2PModule.Infrastructure;
using P2PModule.Interfaces;
using P2PModule.Models;
using P2PModule.Services.Persistence;
using P2PModule.Services.Rating;

namespace P2PModule.Services.Maintenance;

public class ConsensusMergeOperation
{
    public string TargetDate { get; set; } = string.Empty;
    public string MergeDir { get; set; } = string.Empty;        // ConsensusCurrentMerge (finalized iteration results)
    public string IterDir { get; set; } = string.Empty;         // ConsensusCurrentIter (temporary uncommitted staging)
    public string BasePath { get; set; } = string.Empty;
}

/// <summary>
/// Executes atomic disk sharding, delta merges, staging directory isolation, and iteration rollbacks.
/// Incorporates Subjective Logic opinion discounting and fusion math (Belief + Disbelief + Uncertainty = 1.0).
/// Data Pipeline: Incoming Data -> IterDir (staging) -> MergeDir (milestone) -> ConsensusStore (final checkpoint).
/// </summary>
public interface IConsensusMergeEngine
{
    /// <summary>
    /// 1. Start Merge: Initializes staging workspace (IterDir, MergeDir) and loads initial weighted baseline records.
    /// </summary>
    Task<ConsensusMergeOperation> CreateStagingOperationAsync(string targetDateKey);

    /// <summary>
    /// 2. Stage Data: Ingests incoming remote nodes (PeerNodeTransferred) into IterDir as WeightedConsensusNode after cryptographic and Subjective Logic opinion fusion.
    /// </summary>
    Task<bool> StageIncomingNodeSetAsync(ConsensusMergeOperation operation, string remoteNodeId, List<PeerNodeTransferred> remoteNodes);

    /// <summary>
    /// 3. End Iteration: Promotes staged files from IterDir into MergeDir (milestone checkpoint) and clears IterDir.
    /// </summary>
    Task FinalizeIterationAsync(ConsensusMergeOperation operation);

    /// <summary>
    /// 4. End Total Merge: Promotes remaining IterDir to MergeDir, flushes MergeDir into ConsensusStore, and purges staging space.
    /// </summary>
    Task FinalizeTotalMergeAsync(ConsensusMergeOperation operation);

    /// <summary>
    /// 5. Reset Iteration: Drops uncommitted IterDir files and restores state from last MergeDir milestone.
    /// </summary>
    Task ResetIterationMergeAsync(ConsensusMergeOperation operation);

    /// <summary>
    /// 6. Drop Merge: Completely purges the staging workspace (IterDir & MergeDir) without committing to ConsensusStore.
    /// </summary>
    Task AbortMergeOperationAsync(ConsensusMergeOperation operation);

    /// <summary>
    /// Hierarchical lookup: Resolves a peer by checking IterDir -> MergeDir -> ConsensusStore index.
    /// </summary>
    Task<PeerNodeHistoric?> GetNodeFromStagingHierarchyAsync(ConsensusMergeOperation operation, string nodeId);
}

/// <summary>
/// Executes file-first atomic staging, iteration reconciliation, Subjective Logic rating calculations,
/// and total consensus finalize routines without modifying ConsensusStore prior to full completion.
/// </summary>
public class ConsensusMergeEngine : IConsensusMergeEngine
{
    private readonly IConsensusStore _consensusStore;
    private readonly IPeerTracker _peerTracker;
    private readonly IRatingService _ratingService;
    private readonly IOptionsMonitor<P2PSettings> _p2pSettings;
    private readonly IOptionsMonitor<RatingSettings> _ratingSettings;
    private readonly ILogger<ConsensusMergeEngine> _logger;

    public ConsensusMergeEngine(
        IConsensusStore consensusStore,
        IPeerTracker peerTracker,
        IRatingService ratingService,
        IOptionsMonitor<P2PSettings> p2pSettings,
        IOptionsMonitor<RatingSettings> ratingSettings,
        ILogger<ConsensusMergeEngine> logger)
    {
        _consensusStore = consensusStore;
        _peerTracker = peerTracker;
        _ratingService = ratingService;
        _p2pSettings = p2pSettings;
        _ratingSettings = ratingSettings;
        _logger = logger;
    }

    /// <summary>
    /// Scans the consensus staging directory and purges orphaned active merge 
    /// and delta workspaces leftover from previous crashed process runs.
    /// </summary>
    public async Task CleanupOrphanedStagingWorkspacesAsync()
    {
        try
        {
            string basePath = _p2pSettings.CurrentValue.DataStoragePath ?? "./Module_Data/P2P";
            string activeMergesPath = Path.Combine(basePath, "consensus_merges", "active");

            if (!Directory.Exists(activeMergesPath)) return;

            var subDirectories = Directory.GetDirectories(activeMergesPath);
            string todayKey = DateTime.UtcNow.ToString("yyyy-MM-dd");

            foreach (var dir in subDirectories)
            {
                string dirName = Path.GetFileName(dir);

                // Option A: Delete all active workspace folders that don't match today's date
                // Option B: Delete any existing workspace for today if a previous instance crashed mid-run
                if (!string.Equals(dirName, todayKey, StringComparison.OrdinalIgnoreCase) ||
                    IsStaleWorkspace(dir))
                {
                    _logger.LogWarning("[Consensus Cleanup] Found orphaned staging workspace at {Dir}. Purging...", dir);
                    Directory.Delete(dir, recursive: true);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Consensus Cleanup] Exception encountered during startup workspace sweep.");
        }

        await Task.CompletedTask;
    }
    private bool IsStaleWorkspace(string dirPath)
    {
        // A workspace is considered stale if its last write time is older than 2 hours
        var lastWrite = Directory.GetLastWriteTimeUtc(dirPath);
        return (DateTime.UtcNow - lastWrite) > TimeSpan.FromHours(2);
    }

    /// <summary>
    /// 1. Start Merge: Initializes staging workspace (IterDir, MergeDir) for a target date bucket
    /// and populates MergeDir with current baseline WeightedConsensusNode records from PeerTracker / ConsensusStore.
    /// </summary>
    public async Task<ConsensusMergeOperation> CreateStagingOperationAsync(string targetDateKey)
    {
        await CleanupOrphanedStagingWorkspacesAsync();

        if (string.IsNullOrWhiteSpace(targetDateKey))
        {
            targetDateKey = DateTime.UtcNow.ToString("yyyy-MM-dd");
        }

        string basePath = _p2pSettings.CurrentValue.DataStoragePath ?? "./Module_Data/P2P";
        string baseMergeDir = Path.Combine(basePath, "consensus_merges", "active", targetDateKey);

        var operation = new ConsensusMergeOperation
        {
            TargetDate = targetDateKey,
            BasePath = baseMergeDir,
            IterDir = Path.Combine(baseMergeDir, "iter"),
            MergeDir = Path.Combine(baseMergeDir, "merge")
        };

        try
        {
            Directory.CreateDirectory(operation.IterDir);
            Directory.CreateDirectory(operation.MergeDir);

            // Seed MergeDir with active runtime peers as initial baseline milestone state
            var activePeers = _peerTracker.GetLastSeenNodes(PeerTracker.DefaultNodeComparer);
            var jsonOptions = new JsonSerializerOptions { WriteIndented = true };

            foreach (var peer in activePeers)
            {
                if (peer?.Identity?.NodeId != null)
                {
                    var weighted = CreateWeightedNodeFromPeer(peer);
                    string filePath = Path.Combine(operation.MergeDir, $"{peer.Identity.NodeId}.json");
                    string json = JsonSerializer.Serialize(weighted, jsonOptions);
                    await File.WriteAllTextAsync(filePath, json);
                }
            }

            _logger.LogInformation("[Consensus Engine] Staging workspace initialized for date {Date} at {Path}", targetDateKey, baseMergeDir);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Consensus Engine] Failed to create staging directories for date {Date}", targetDateKey);
            throw;
        }

        return operation;
    }

    /// <summary>
    /// 2. Stage Data: Ingests incoming remote transferred nodes (PeerNodeTransferred) directly into IterDir as WeightedConsensusNode.
    /// Performs cryptographic verification and applies Subjective Logic opinion discounting/fusion math based on the witness sender's trust score.
    /// </summary>
    public async Task<bool> StageIncomingNodeSetAsync(
        ConsensusMergeOperation operation,
        string remoteNodeId,
        List<PeerNodeTransferred> remoteNodes)
    {
        if (operation == null || remoteNodes == null || remoteNodes.Count == 0)
        {
            return false;
        }

        _logger.LogInformation("[Consensus Engine] Staging incoming node set from {RemoteNodeId} into IterDir. Count: {Count}.",
            remoteNodeId, remoteNodes.Count);

        // Resolve or derive the witness sender's Subjective Logic opinion state
        WeightedConsensusNode? senderNode = await GetWeightedNodeFromStagingHierarchyAsync(operation, remoteNodeId);
        if (senderNode == null && !string.IsNullOrEmpty(remoteNodeId))
        {
            var senderPeer = _peerTracker.GetPeer(remoteNodeId);
            if (senderPeer != null)
            {
                senderNode = CreateWeightedNodeFromPeer(senderPeer);
            }
        }

        // Default neutral sender anchor if witness node is unindexed
        senderNode ??= new WeightedConsensusNode
        {
            Belief = 0.5,
            Disbelief = 0.0,
            Uncertainty = 0.5,
            NodeData = new PeerNodeHistoric { TrustScore = 25 }
        };

        int stagedCount = 0;
        int rejectedCount = 0;
        var jsonOptions = new JsonSerializerOptions { WriteIndented = true };

        foreach (var remoteNode in remoteNodes)
        {
            if (remoteNode?.Identity == null || string.IsNullOrEmpty(remoteNode.Identity.NodeId))
            {
                rejectedCount++;
                continue;
            }

            string nodeId = remoteNode.Identity.NodeId;
            byte[] nodeHash = remoteNode.Identity.GetNodeDataHash();

            // 1. Verify internal self-signature on the remote node identity
            bool isSelfSignatureValid = remoteNode.Identity.PublicVerifyKey != null &&
                                        remoteNode.Identity.Signature != null &&
                                        remoteNode.Identity.Signature.Length > 0 &&
                                        P2PCrypto.TestSignature(nodeHash, remoteNode.Identity.PublicVerifyKey, remoteNode.Identity.Signature);

            if (!isSelfSignatureValid)
            {
                _logger.LogWarning("[Consensus Engine] Rejecting node {NodeId} - Self-signature verification failed.", nodeId);

                // Apply minor penalty to witness sender for relaying corrupt signature payload
                ApplySenderSlanderPenalty(senderNode, operation, 0.05);
                rejectedCount++;
                continue;
            }

            // 2. Check cryptographic lineage against local history
            bool isLineageVerified = VerifyHistoricalSignatures(remoteNode, nodeHash, nodeId);

            // 3. Resolve existing weighted node record from current staging hierarchy
            var existingWeighted = await GetWeightedNodeFromStagingHierarchyAsync(operation, nodeId);

            WeightedConsensusNode targetWeighted;

            if (existingWeighted?.NodeData?.Identity != null)
            {
                // Drop stale or replayed updates
                if (remoteNode.Identity.SignatureTime < existingWeighted.NodeData.Identity.SignatureTime)
                {
                    continue;
                }

                targetWeighted = existingWeighted;

                if (remoteNode.Identity.SignatureTime > targetWeighted.NodeData.Identity.SignatureTime)
                {
                    targetWeighted.NodeData.Identity = remoteNode.Identity;
                }

                targetWeighted.NodeData.IsVerified = isLineageVerified || targetWeighted.NodeData.IsVerified;
            }
            else
            {
                targetWeighted = new WeightedConsensusNode
                {
                    NodeData = new PeerNodeHistoric(remoteNode)
                    {
                        IsVerified = isLineageVerified
                    },
                    Belief = 0.5,
                    Disbelief = 0.0,
                    Uncertainty = 0.5
                };
            }

            if (!string.IsNullOrEmpty(remoteNodeId))
            {
                targetWeighted.NodeData.LastSeenByNodeId.Add(remoteNodeId);
            }

            // 4. Perform Subjective Logic Opinion Fusion based on the witness sender's recommendation
            targetWeighted = MergeNodeRatingBasedOnSender(targetWeighted, remoteNode, senderNode);

            // 5. Persist historic record exclusively to IterDir (iteration staging)
            try
            {
                string filePath = Path.Combine(operation.IterDir, $"{nodeId}.json");
                string json = JsonSerializer.Serialize(targetWeighted, jsonOptions);
                await File.WriteAllTextAsync(filePath, json);
                stagedCount++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Consensus Engine] Error writing staged file for node {NodeId} in IterDir.", nodeId);
                rejectedCount++;
            }
        }

        _logger.LogInformation("[Consensus Engine] Staging complete. Staged into IterDir: {Staged}, Rejected: {Rejected}.",
            stagedCount, rejectedCount);

        return stagedCount > 0;
    }

    /// <summary>
    /// 3. End Iteration: Promotes staged files from IterDir into MergeDir (milestone checkpoint) and clears IterDir.
    /// </summary>
    public async Task FinalizeIterationAsync(ConsensusMergeOperation operation)
    {
        if (operation == null) return;

        _logger.LogInformation("[Consensus Engine] Finalizing iteration. Promoting IterDir -> MergeDir milestone for date {Date}.", operation.TargetDate);

        try
        {
            if (Directory.Exists(operation.IterDir))
            {
                foreach (var iterFile in Directory.GetFiles(operation.IterDir))
                {
                    string fileName = Path.GetFileName(iterFile);
                    string mergeFile = Path.Combine(operation.MergeDir, fileName);

                    // Overwrite iteration changes into MergeDir milestone checkpoint
                    File.Copy(iterFile, mergeFile, overwrite: true);
                    File.Delete(iterFile); // Clear IterDir staging
                }
            }

            _logger.LogInformation("[Consensus Engine] Iteration milestone successfully updated in MergeDir for date {Date}.", operation.TargetDate);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Consensus Engine] Exception during iteration finalize (IterDir -> MergeDir) for date {Date}.", operation.TargetDate);
            throw;
        }

        await Task.CompletedTask;
    }

    /// <summary>
    /// 4. End Total Merge: Flushes remaining IterDir into MergeDir, commits MergeDir into ConsensusStore 
    /// (the final checkpoint), and purges temporary staging directories.
    /// </summary>
    public async Task FinalizeTotalMergeAsync(ConsensusMergeOperation operation)
    {
        if (operation == null) return;

        _logger.LogInformation("[Consensus Engine] Finalizing total consensus merge for date {Date}. Committing MergeDir to ConsensusStore.", operation.TargetDate);

        try
        {
            // Promote any remaining uncommitted iteration files to the milestone directory
            await FinalizeIterationAsync(operation);

            // Read all validated milestone files from MergeDir and write to ConsensusStore
            if (Directory.Exists(operation.MergeDir))
            {
                foreach (var mergeFile in Directory.GetFiles(operation.MergeDir))
                {
                    string nodeId = Path.GetFileNameWithoutExtension(mergeFile);
                    string json = await File.ReadAllTextAsync(mergeFile);

                    // Unpack WeightedConsensusNode or fallback PeerNodeHistoric
                    PeerNodeHistoric? peerToStore = null;
                    try
                    {
                        var weighted = JsonSerializer.Deserialize<WeightedConsensusNode>(json);
                        peerToStore = weighted?.NodeData;
                    }
                    catch
                    {
                        peerToStore = JsonSerializer.Deserialize<PeerNodeHistoric>(json);
                    }

                    if (peerToStore != null && !string.IsNullOrEmpty(nodeId))
                    {
                        _consensusStore.UpdateHistoricalPeer(operation.TargetDate, nodeId, peerToStore);
                        await _peerTracker.UpdatePeerAsync(peerToStore.Identity);
                    }
                }
            }

            // Purge staging workspaces (IterDir & MergeDir)
            if (Directory.Exists(operation.BasePath))
            {
                Directory.Delete(operation.BasePath, recursive: true);
            }

            _logger.LogInformation("[Consensus Engine] Total consensus merge finalized and committed to ConsensusStore for date {Date}.", operation.TargetDate);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Consensus Engine] Exception during total merge finalize for date {Date}.", operation.TargetDate);
            throw;
        }
    }

    /// <summary>
    /// 5. Reset Iteration: Drops uncommitted IterDir files and restores files from the MergeDir milestone.
    /// </summary>
    public Task ResetIterationMergeAsync(ConsensusMergeOperation operation)
    {
        if (operation == null) return Task.CompletedTask;

        _logger.LogWarning("[Consensus Engine] Iteration rollback triggered for date {Date}. Resetting IterDir to MergeDir milestone state.", operation.TargetDate);

        try
        {
            // Clear current uncommitted iteration files
            if (Directory.Exists(operation.IterDir))
            {
                foreach (var file in Directory.GetFiles(operation.IterDir))
                {
                    File.Delete(file);
                }
            }

            // Restore state from last milestone checkpoint in MergeDir back to IterDir
            if (Directory.Exists(operation.MergeDir) && Directory.Exists(operation.IterDir))
            {
                foreach (var file in Directory.GetFiles(operation.MergeDir))
                {
                    string fileName = Path.GetFileName(file);
                    File.Copy(file, Path.Combine(operation.IterDir, fileName), overwrite: true);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Consensus Engine] Exception encountered during iteration rollback procedure for date {Date}.", operation.TargetDate);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// 6. Drop Merge: Completely purges the staging workspace (IterDir & MergeDir) without modifying ConsensusStore.
    /// </summary>
    public Task AbortMergeOperationAsync(ConsensusMergeOperation operation)
    {
        if (operation == null) return Task.CompletedTask;

        _logger.LogWarning("[Consensus Engine] Aborting consensus merge operation for date {Date}. Purging staging workspace.", operation.TargetDate);

        try
        {
            if (Directory.Exists(operation.BasePath))
            {
                Directory.Delete(operation.BasePath, recursive: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Consensus Engine] Exception during abort merge operation for date {Date}.", operation.TargetDate);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Hierarchical lookup order: IterDir -> MergeDir -> ConsensusStore.
    /// Returns the unwrapped PeerNodeHistoric view.
    /// </summary>
    public async Task<PeerNodeHistoric?> GetNodeFromStagingHierarchyAsync(ConsensusMergeOperation operation, string nodeId)
    {
        var weighted = await GetWeightedNodeFromStagingHierarchyAsync(operation, nodeId);
        return weighted?.NodeData;
    }

    /// <summary>
    /// Resolves the raw WeightedConsensusNode envelope across IterDir -> MergeDir -> ConsensusStore.
    /// </summary>
    private async Task<WeightedConsensusNode?> GetWeightedNodeFromStagingHierarchyAsync(ConsensusMergeOperation operation, string nodeId)
    {
        if (operation == null || string.IsNullOrEmpty(nodeId)) return null;

        // 1. Check current iteration directory (IterDir)
        if (Directory.Exists(operation.IterDir))
        {
            string iterPath = Path.Combine(operation.IterDir, $"{nodeId}.json");
            if (File.Exists(iterPath))
            {
                try
                {
                    string json = await File.ReadAllTextAsync(iterPath);
                    var weighted = JsonSerializer.Deserialize<WeightedConsensusNode>(json);
                    if (weighted?.NodeData != null) return weighted;
                }
                catch { }
            }
        }

        // 2. Check milestone directory (MergeDir)
        if (Directory.Exists(operation.MergeDir))
        {
            string mergePath = Path.Combine(operation.MergeDir, $"{nodeId}.json");
            if (File.Exists(mergePath))
            {
                try
                {
                    string json = await File.ReadAllTextAsync(mergePath);
                    var weighted = JsonSerializer.Deserialize<WeightedConsensusNode>(json);
                    if (weighted?.NodeData != null) return weighted;
                }
                catch { }
            }
        }

        // 3. Fallback to ConsensusStore master index
        var historic = _consensusStore.GetHistoricalPeer(operation.TargetDate, nodeId);
        if (historic != null)
        {
            return CreateWeightedNodeFromHistoric(historic);
        }

        return null;
    }

    /// <summary>
    /// Constructs a WeightedConsensusNode from runtime PeerNodeHistoric applying Seniority Shielding.
    /// </summary>
    private WeightedConsensusNode CreateWeightedNodeFromPeer(PeerNodeHistoric peer)
    {
        double smoothing = _ratingSettings.CurrentValue.SenioritySmoothingCoefficient;
        if (smoothing <= 0) smoothing = 7.0;

        double seniority = _ratingService.GetNodeHistoricalSeniority(peer.Identity.NodeId);
        double shield = seniority > 0 ? seniority / (seniority + smoothing) : 0.0;

        // Bounded conservation rule: b + d + u = 1.0
        double uncertainty = Math.Clamp(1.0 - shield, 0.0, 1.0);
        double availableMassPool = 1.0 - uncertainty;

        // Remap TrustScore [-50, 100] to expected probability baseline [0.0, 1.0]
        double baseExpectation = Math.Clamp((peer.TrustScore + 50.0) / 150.0, 0.0, 1.0);

        double belief = availableMassPool > 0 ? baseExpectation * availableMassPool : 0.0;
        double disbelief = availableMassPool > 0 ? (1.0 - baseExpectation) * availableMassPool : 0.0;

        return new WeightedConsensusNode
        {
            NodeData = peer,
            Belief = Math.Clamp(belief, 0.0, 1.0),
            Disbelief = Math.Clamp(disbelief, 0.0, 1.0),
            Uncertainty = uncertainty
        };
    }

    private WeightedConsensusNode CreateWeightedNodeFromHistoric(PeerNodeHistoric peer)
    {
        return CreateWeightedNodeFromPeer(peer);
    }

    /// <summary>
    /// Applies Subjective Logic recommendation discounting and cumulative consensus fusion rules.
    /// Merges incoming node rating with local state based on the witness sender's trust profile.
    /// </summary>
    private WeightedConsensusNode MergeNodeRatingBasedOnSender(
        WeightedConsensusNode localState,
        PeerNodeTransferred incomingNode,
        WeightedConsensusNode senderNode)
    {
        double bSender = senderNode.Belief;
        double dSender = senderNode.Disbelief;
        double uSender = senderNode.Uncertainty;

        // Convert incoming trust score from [-50, 100] to expected probability range [0.0, 1.0]
        double eIncoming = Math.Clamp((incomingNode.TrustScore + 50.0) / 150.0, 0.0, 1.0);
        double bIncoming = eIncoming * 0.8;
        double dIncoming = (1.0 - eIncoming) * 0.8;
        double uIncoming = 0.2;

        // 1. Recommendation Discounting Rule: Omega_C^{A:B} = Omega_B^A x Omega_C^B
        double bDiscounted = bSender * bIncoming;
        double dDiscounted = bSender * dIncoming;
        double uDiscounted = dSender + uSender + (bSender * uIncoming);

        // 2. Cumulative Consensus Fusion Rule
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

        // 3. Derived fused expectation and convert back to TrustScore scale [-50, 100]
        double eMerged = bMerged + (0.5 * uMerged);
        int mergedTrust = (int)Math.Clamp(Math.Floor(-50.0 + (150.0 * eMerged)), -50, 100);

        localState.Belief = Math.Clamp(bMerged, 0.0, 1.0);
        localState.Disbelief = Math.Clamp(dMerged, 0.0, 1.0);
        localState.Uncertainty = Math.Clamp(uMerged, 0.0, 1.0);
        localState.NodeData.TrustScore = mergedTrust;

        return localState;
    }

    /// <summary>
    /// Penalizes witness sender belief/trust score when relaying invalid or corrupt identity payloads.
    /// </summary>
    private void ApplySenderSlanderPenalty(WeightedConsensusNode senderNode, ConsensusMergeOperation operation, double penaltyRate)
    {
        senderNode.Belief *= (1.0 - penaltyRate);
        senderNode.Disbelief = (senderNode.Disbelief * (1.0 - penaltyRate)) + penaltyRate;
        senderNode.Uncertainty = Math.Max(0.0, 1.0 - (senderNode.Belief + senderNode.Disbelief));

        double currentExpectation = senderNode.Belief + (0.5 * senderNode.Uncertainty);
        senderNode.NodeData.TrustScore = (int)Math.Clamp(Math.Floor(-50.0 + (150.0 * currentExpectation)), -50, 100);

        try
        {
            string senderFilePath = Path.Combine(operation.IterDir, $"{senderNode.NodeData.Identity.NodeId}.json");
            string json = JsonSerializer.Serialize(senderNode, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(senderFilePath, json);
        }
        catch { }
    }

    /// <summary>
    /// Verifies historical signature envelopes against frozen historical keys in ConsensusStore.
    /// </summary>
    private bool VerifyHistoricalSignatures(PeerNodeTransferred candidateNode, byte[] nodeHash, string targetNodeId)
    {
        if (candidateNode.Signatures == null || candidateNode.Signatures.Count == 0)
            return false;

        foreach (var envelope in candidateNode.Signatures)
        {
            if (envelope == null || string.IsNullOrEmpty(envelope.DayKey) || envelope.Signature == null || envelope.Signature.Length == 0)
                continue;

            var histPeer = _consensusStore.GetHistoricalPeer(envelope.DayKey, targetNodeId);
            if (histPeer?.Identity?.PublicVerifyKey == null || histPeer.Identity.PublicVerifyKey.Length == 0)
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

            if (P2PCrypto.TestSignature(signedPayload, histPeer.Identity.PublicVerifyKey, envelope.Signature))
            {
                return true; // Direct lineage verified
            }
        }

        return false;
    }
}