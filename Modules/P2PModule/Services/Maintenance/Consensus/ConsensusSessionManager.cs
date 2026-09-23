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
/// </summary>
public class ConsensusMergeSession
{
    public Guid SessionId { get; set; } = Guid.NewGuid();
    public string TargetSyncDate { get; set; } = DateTime.UtcNow.ToString("yyyy-MM-dd");
    public string InitiatorNodeId { get; set; } = string.Empty;

    public string TargetNodeId { get; set; } = string.Empty;
    public string GeneratedDeltasDir { get; set; } = string.Empty;
    public string DownloadedDeltasDir { get; set; } = string.Empty;

    public byte[] DayKeyPrivateKey { get; set; } = Array.Empty<byte>();
    public byte[] DayKeyPublicKey { get; set; } = Array.Empty<byte>();
    public byte[] DayKeySignatureKey { get; set; } = Array.Empty<byte>();
    public byte[] DayKeyVerifyKey { get; set; } = Array.Empty<byte>();

    public HashSet<string> PastSyncSet { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> CurrentSyncSet { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> VisitedNodes { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public ConcurrentDictionary<string, ChainSignature> SyncSetSignatures { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public ConcurrentQueue<string> NodesToVisit { get; set; } = new();

    public ConcurrentDictionary<string, ConsensusSessionDeltaItem> LocalDeltas { get; set; } = new();
    public ConcurrentDictionary<string, ConsensusSessionDeltaItem> PendingDeltasToMerge { get; set; } = new();
    public ConcurrentDictionary<string, Task> ActiveDeltaDownloads { get; set; } = new();

    public ConsensusMergeOperation? StagingOperation { get; set; }
    public DateTime LastActivityUtc { get; set; } = DateTime.UtcNow;
}

public interface IConsensusSessionManager
{
    bool TryAcquireSession(string initiatorNodeId, out ConsensusMergeSession? session);
    ConsensusMergeSession? GetActiveSession();
    bool IsBusy();
    void SetActiveTargetNode(string? targetNodeId);
    void QueueNodesToVisit(IEnumerable<string> nodeIds);
    bool TryPopNextNodeToVisit(out string? nodeId);
    void MarkNodeAsVisited(string nodeId);
    void RegisterNodesInCurrentSyncSet(IEnumerable<string> nodeIds);
    void AddSyncSetSignature(string signerNodeId, string targetNodeId, byte[] signature, int order = 0);
    List<ChainSignature> GetSyncSetSignatures();
    List<string> GetPastSyncSetNodes();
    Task<ConsensusSessionDeltaItem?> CreateLocalDeltaAsync(List<ChainSignature>? signatureChain = null);
    bool PrepareDownloadedDeltaFolder(out string deltaId, out string destFolderPath);
    bool RegisterDownloadedDelta(string sourceNodeId, string downloadedFolderPath, List<ChainSignature>? signatureChain = null);
    List<ConsensusSessionDeltaItem> GetPendingDeltasToMerge();
    bool HasPendingDeltas();
    bool RemovePendingDelta(string deltaId, bool deleteFolder = false);
    ConsensusSessionDeltaItem? GetLocalDelta(string deltaId);
    List<string> GetDeltaFolderPathsBatch(List<string> deltaIds);
    Task<PeerNodeHistoric?> GetNodeFromDeltaOrStagingHierarchyAsync(string deltaId, string nodeId);
    Task FinalizeIterationStepAsync();
    Task FinalizeSessionAsync();
    Task AbortSessionAsync();
}


public class ConsensusSessionManager : IConsensusSessionManager
{
    private readonly IConsensusMergeEngine _mergeEngine;
    private readonly INodeIdentityService _identity;
    private readonly IPeerTracker _peerTracker;
    private readonly ILogger<ConsensusSessionManager> _logger;

    private readonly object _sessionLock = new();
    private ConsensusMergeSession? _activeSession;

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

    public bool TryAcquireSession(string initiatorNodeId, out ConsensusMergeSession? session)
    {
        lock (_sessionLock)
        {
            if (_activeSession != null)
            {
                _logger.LogWarning("[Session Manager] Rejecting session request from {Initiator} - Active session {SessionId} is already running (BUSY).",
                    initiatorNodeId, _activeSession.SessionId);
                session = null;
                return false;
            }

            string todayKey = DateTime.UtcNow.ToString("yyyy-MM-dd");
            var stagingOp = _mergeEngine.CreateStagingOperationAsync(todayKey).GetAwaiter().GetResult();

            _activeSession = new ConsensusMergeSession
            {
                TargetSyncDate = todayKey,
                InitiatorNodeId = initiatorNodeId,
                LastActivityUtc = DateTime.UtcNow,

                StagingOperation = stagingOp,
                GeneratedDeltasDir = Path.Combine(stagingOp.BasePath, "generated_deltas"),
                DownloadedDeltasDir = Path.Combine(stagingOp.BasePath, "downloaded_deltas"),

                DayKeyPrivateKey = _identity.PrivateKey ?? Array.Empty<byte>(),
                DayKeyPublicKey = _identity.PublicKey ?? Array.Empty<byte>(),
                DayKeySignatureKey = _identity.PrivateSignatureKey ?? Array.Empty<byte>(),
                DayKeyVerifyKey = _identity.PublicVerifyKey ?? Array.Empty<byte>()
            };

            Directory.CreateDirectory(_activeSession.GeneratedDeltasDir);
            Directory.CreateDirectory(_activeSession.DownloadedDeltasDir);

            _activeSession.PastSyncSet.Add(_identity.NodeId);
            _activeSession.VisitedNodes.Add(_identity.NodeId);

            _logger.LogInformation("[Session Manager] Consensus session {SessionId} acquired for date {Date} by {Initiator}. BasePath: {BasePath}.",
                _activeSession.SessionId, todayKey, initiatorNodeId, stagingOp.BasePath);

            session = _activeSession;
            return true;
        }
    }

    public ConsensusMergeSession? GetActiveSession()
    {
        lock (_sessionLock)
        {
            return _activeSession;
        }
    }

    public bool IsBusy()
    {
        lock (_sessionLock)
        {
            return _activeSession != null;
        }
    }

    public void SetActiveTargetNode(string? targetNodeId)
    {
        lock (_sessionLock)
        {
            if (_activeSession != null)
            {
                _activeSession.TargetNodeId = targetNodeId ?? string.Empty;
                _activeSession.LastActivityUtc = DateTime.UtcNow;
            }
        }
    }

    public void QueueNodesToVisit(IEnumerable<string> nodeIds)
    {
        lock (_sessionLock)
        {
            if (_activeSession == null || nodeIds == null) return;

            foreach (var id in nodeIds)
            {
                if (string.IsNullOrWhiteSpace(id)) continue;

                if (!_activeSession.VisitedNodes.Contains(id) &&
                    !_activeSession.PastSyncSet.Contains(id) &&
                    !_activeSession.CurrentSyncSet.Contains(id) &&
                    !_activeSession.NodesToVisit.Contains(id, StringComparer.OrdinalIgnoreCase))
                {
                    _activeSession.NodesToVisit.Enqueue(id);
                }
            }

            _activeSession.LastActivityUtc = DateTime.UtcNow;
        }
    }

    public bool TryPopNextNodeToVisit(out string? nodeId)
    {
        lock (_sessionLock)
        {
            if (_activeSession == null)
            {
                nodeId = null;
                return false;
            }

            while (_activeSession.NodesToVisit.TryDequeue(out var candidate))
            {
                if (string.IsNullOrWhiteSpace(candidate)) continue;

                if (!_activeSession.VisitedNodes.Contains(candidate) &&
                    !_activeSession.PastSyncSet.Contains(candidate) &&
                    !_activeSession.CurrentSyncSet.Contains(candidate))
                {
                    _activeSession.VisitedNodes.Add(candidate);
                    _activeSession.LastActivityUtc = DateTime.UtcNow;
                    nodeId = candidate;
                    return true;
                }
            }

            nodeId = null;
            return false;
        }
    }

    public void MarkNodeAsVisited(string nodeId)
    {
        lock (_sessionLock)
        {
            if (_activeSession == null || string.IsNullOrWhiteSpace(nodeId)) return;

            _activeSession.VisitedNodes.Add(nodeId);
            _activeSession.LastActivityUtc = DateTime.UtcNow;
        }
    }

    public void RegisterNodesInCurrentSyncSet(IEnumerable<string> nodeIds)
    {
        lock (_sessionLock)
        {
            if (_activeSession == null || nodeIds == null) return;

            foreach (var id in nodeIds)
            {
                if (!string.IsNullOrWhiteSpace(id))
                {
                    _activeSession.CurrentSyncSet.Add(id);
                }
            }

            _activeSession.LastActivityUtc = DateTime.UtcNow;
        }
    }

    public void AddSyncSetSignature(string signerNodeId, string targetNodeId, byte[] signature, int order = 0)
    {
        lock (_sessionLock)
        {
            if (_activeSession == null || string.IsNullOrWhiteSpace(signerNodeId)) return;

            var item = new ChainSignature
            {
                Order = order != 0 ? order : _activeSession.SyncSetSignatures.Count,
                SignerNodeId = signerNodeId,
                TargetNodeId = targetNodeId,
                Signature = signature ?? Array.Empty<byte>(),
                TimestampUtc = DateTime.UtcNow
            };

            _activeSession.SyncSetSignatures[signerNodeId] = item;
            _activeSession.LastActivityUtc = DateTime.UtcNow;
        }
    }

    public List<ChainSignature> GetSyncSetSignatures()
    {
        lock (_sessionLock)
        {
            if (_activeSession == null) return new List<ChainSignature>();
            return _activeSession.SyncSetSignatures.Values.OrderBy(x => x.Order).ToList();
        }
    }

    public List<string> GetPastSyncSetNodes()
    {
        lock (_sessionLock)
        {
            if (_activeSession == null) return new List<string>();
            return _activeSession.PastSyncSet.ToList();
        }
    }


    public async Task<ConsensusSessionDeltaItem?> CreateLocalDeltaAsync(List<ChainSignature>? signatureChain = null)
    {
        ConsensusMergeSession? session;
        lock (_sessionLock)
        {
            session = _activeSession;
        }

        if (session == null || session.StagingOperation == null) return null;

        string iterDir = session.StagingOperation.IterDir;
        string mergeDir = session.StagingOperation.MergeDir;

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
                try
                {
                    var weighted = JsonSerializer.Deserialize<WeightedConsensusNode>(iterJson);
                    iterNode = weighted?.NodeData;
                }
                catch
                {
                    iterNode = JsonSerializer.Deserialize<PeerNodeHistoric>(iterJson);
                }

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
                    try
                    {
                        var weightedMerge = JsonSerializer.Deserialize<WeightedConsensusNode>(mergeJson);
                        mergeNode = weightedMerge?.NodeData;
                    }
                    catch
                    {
                        mergeNode = JsonSerializer.Deserialize<PeerNodeHistoric>(mergeJson);
                    }

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

    public bool PrepareDownloadedDeltaFolder(out string deltaId, out string destFolderPath)
    {
        ConsensusMergeSession? session;
        lock (_sessionLock)
        {
            session = _activeSession;
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

    public bool RegisterDownloadedDelta(string sourceNodeId, string downloadedFolderPath, List<ChainSignature>? signatureChain = null)
    {
        ConsensusMergeSession? session;
        lock (_sessionLock)
        {
            session = _activeSession;
        }

        if (session == null || !Directory.Exists(downloadedFolderPath)) return false;

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

        bool isChainValid = VerifySignatureChain(chain, nodeVerifyKeys, session);
        if (!isChainValid)
        {
            _logger.LogWarning("[Session Manager] Early verification failed for downloaded delta {DeltaId} from {SourceNodeId}. Purging folder.", deltaId, sourceNodeId);
            try
            {
                Directory.Delete(downloadedFolderPath, recursive: true);
            }
            catch { }
            return false;
        }

        var deltaItem = new ConsensusSessionDeltaItem
        {
            DeltaId = deltaId,
            SourceNodeId = sourceNodeId,
            CreatedUtc = incomingMetadata?.CreatedUtc ?? DateTime.UtcNow,
            DeltaFolderPath = downloadedFolderPath,
            SignatureChain = chain,
            Signature = incomingMetadata?.Signature ?? Array.Empty<byte>()
        };

        File.WriteAllText(sourceMetadataFile, JsonSerializer.Serialize(deltaItem, new JsonSerializerOptions { WriteIndented = true }));

        session.PendingDeltasToMerge[deltaId] = deltaItem;
        session.LastActivityUtc = DateTime.UtcNow;

        _logger.LogInformation("[Session Manager] Early verification passed. Registered downloaded delta {DeltaId} from {SourceNodeId}.", deltaId, sourceNodeId);
        return true;
    }

    public List<ConsensusSessionDeltaItem> GetPendingDeltasToMerge()
    {
        lock (_sessionLock)
        {
            if (_activeSession == null) return new List<ConsensusSessionDeltaItem>();
            return _activeSession.PendingDeltasToMerge.Values.ToList();
        }
    }

    public bool HasPendingDeltas()
    {
        lock (_sessionLock)
        {
            if (_activeSession == null) return false;
            return !_activeSession.PendingDeltasToMerge.IsEmpty;
        }
    }

    public bool RemovePendingDelta(string deltaId, bool deleteFolder = false)
    {
        lock (_sessionLock)
        {
            if (_activeSession == null || string.IsNullOrEmpty(deltaId)) return false;

            bool removed = _activeSession.PendingDeltasToMerge.TryRemove(deltaId, out var deltaItem);
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

    public ConsensusSessionDeltaItem? GetLocalDelta(string deltaId)
    {
        lock (_sessionLock)
        {
            if (_activeSession == null || string.IsNullOrEmpty(deltaId)) return null;
            _activeSession.LocalDeltas.TryGetValue(deltaId, out var deltaItem);
            return deltaItem;
        }
    }

    public List<string> GetDeltaFolderPathsBatch(List<string> deltaIds)
    {
        var folderPaths = new List<string>();
        lock (_sessionLock)
        {
            if (_activeSession == null || deltaIds == null || deltaIds.Count == 0) return folderPaths;

            foreach (var id in deltaIds)
            {
                if (_activeSession.LocalDeltas.TryGetValue(id, out var delta) && Directory.Exists(delta.DeltaFolderPath))
                {
                    folderPaths.Add(delta.DeltaFolderPath);
                }
            }
        }

        return folderPaths;
    }


    public async Task<PeerNodeHistoric?> GetNodeFromDeltaOrStagingHierarchyAsync(string deltaId, string nodeId)
    {
        ConsensusMergeSession? session;
        lock (_sessionLock)
        {
            session = _activeSession;
        }

        if (session == null || string.IsNullOrEmpty(nodeId)) return null;

        if (!string.IsNullOrEmpty(deltaId))
        {
            ConsensusSessionDeltaItem? deltaItem = GetLocalDelta(deltaId);
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
                        try
                        {
                            var weighted = JsonSerializer.Deserialize<WeightedConsensusNode>(json);
                            peerFromDelta = weighted?.NodeData;
                        }
                        catch
                        {
                            peerFromDelta = JsonSerializer.Deserialize<PeerNodeHistoric>(json);
                        }

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

    public async Task FinalizeIterationStepAsync()
    {
        ConsensusMergeSession? session;
        lock (_sessionLock)
        {
            session = _activeSession;
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

    public async Task FinalizeSessionAsync()
    {
        ConsensusMergeSession? sessionToFinalize;

        lock (_sessionLock)
        {
            sessionToFinalize = _activeSession;
            _activeSession = null;
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

    public async Task AbortSessionAsync()
    {
        ConsensusMergeSession? sessionToAbort;

        lock (_sessionLock)
        {
            sessionToAbort = _activeSession;
            _activeSession = null;
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
                try
                {
                    var weighted = JsonSerializer.Deserialize<WeightedConsensusNode>(json);
                    peer = weighted?.NodeData;
                }
                catch
                {
                    peer = JsonSerializer.Deserialize<PeerNodeHistoric>(json);
                }

                if (peer?.Identity != null && !string.IsNullOrEmpty(peer.Identity.NodeId) && peer.Identity.PublicVerifyKey?.Length > 0)
                {
                    result[peer.Identity.NodeId] = peer.Identity.PublicVerifyKey;
                }
            }
            catch { }
        }

        return result;
    }

    private bool VerifySignatureChain(List<ChainSignature> chain, Dictionary<string, byte[]> nodeVerifyKeys, ConsensusMergeSession? session)
    {
        if (chain == null || chain.Count == 0) return true;

        var sortedChain = chain.OrderBy(x => x.Order).ToList();
        byte[]? previousSignature = null;

        foreach (var sig in sortedChain)
        {
            if (sig == null || string.IsNullOrEmpty(sig.SignerNodeId)) return false;

            if (!nodeVerifyKeys.TryGetValue(sig.SignerNodeId, out var publicVerifyKey) || publicVerifyKey == null || publicVerifyKey.Length == 0)
            {
                if (session?.StagingOperation != null)
                {
                    var historicPeer = _mergeEngine.GetNodeFromStagingHierarchyAsync(session.StagingOperation, sig.SignerNodeId).GetAwaiter().GetResult();
                    if (historicPeer?.Identity?.PublicVerifyKey != null && historicPeer.Identity.PublicVerifyKey.Length > 0)
                    {
                        publicVerifyKey = historicPeer.Identity.PublicVerifyKey;
                    }
                }

                if (publicVerifyKey == null || publicVerifyKey.Length == 0)
                {
                    var localPeer = _peerTracker.GetPeer(sig.SignerNodeId);
                    if (localPeer?.Identity?.PublicVerifyKey != null && localPeer.Identity.PublicVerifyKey.Length > 0)
                    {
                        publicVerifyKey = localPeer.Identity.PublicVerifyKey;
                    }
                    else
                    {
                        return false;
                    }
                }
            }

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

            byte[] payloadHash = SHA256.HashData(ms.ToArray());
            if (!P2PCrypto.TestSignature(payloadHash, publicVerifyKey, sig.Signature))
            {
                return false;
            }

            previousSignature = sig.Signature;
        }

        return true;
    }
}