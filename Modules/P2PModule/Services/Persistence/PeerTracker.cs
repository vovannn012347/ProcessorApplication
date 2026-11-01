// [full-implementation]
using System.Collections.Concurrent;
using System.Text.Json;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using P2PModule.Configuration;
using P2PModule.Interfaces;
using P2PModule.Models;
using ProcessorApplication.Utils;

namespace P2PModule.Services.Persistence;

public class PeerTrackerIndex
{
    public DateTime TodayIndexData { get; set; } = DateTime.UtcNow;
    public string TodayIndex { get; set; } = DateTime.Now.ToString("yyyy-MM-dd");
}


public interface IPeerTracker
{
    Task InitializeAsync();
    IEnumerable<PeerNodeHistoric> GetLastSeenNodes(Comparison<PeerNodeHistoric> sortingRule);
    IEnumerable<PeerNodeHistoric> GetAvailableNodes(Comparison<PeerNodeHistoric> sortingRule);
    PeerNodeRuntime? GetPeer(string nodeId, bool historic = true);
    NodePersistentState? GetPeerIndex(string nodeId);
    bool IsPeerExists(string nodeId);
    Task UpdatePeerAsync(PeerImmutableData identity);
    Task UpdatePeerAsync(PeerNodeRuntime node);
    Task UpdatePeerActivityAsync(string nodeId);
    Task UpdatePeerWasSeenByAsync(string nodeId, string seenBy);
    Task UpdateTrustAsync(string nodeId, double amount);
    void RemovePeer(string nodeId);

    // New API Methods added for explicit Epoch Vector manipulation and querying
    List<string> GetRecordedEpochsForNode(string nodeId);
    string GetMostRecentEpochForNode(string nodeId);

    Task FlushPendingUpdatesToDiskAsync();
    void PruneHistory();

    event Action<PeerNodeRuntime> OnPeerUpdate;

    event Action<PeerNodeRuntime> OnPeerNew;

    //event Action<PeerNodeRuntime> OnPeerOutdated;
}
/// <summary>
/// Manages high-frequency fluid runtime operations over /currentIndex and /nodeupdateindex in _baseDir.
/// Utilizes IConsensusStore history paths as backfall frames for tracking context validation loops.
/// </summary>
public class PeerTracker : IPeerTracker, IDisposable, IAsyncDisposable
{
    public static readonly Comparison<PeerNodeHistoric> DefaultNodeComparer = (a, b) =>
    {
        int scoreCompare = b.TrustScore.CompareTo(a.TrustScore);
        if (scoreCompare != 0) return scoreCompare;
        return b.LastSeen.CompareTo(a.LastSeen);
    };

    private readonly IConsensusStore _archiveStore;
    private readonly IOptionsMonitor<P2PSettings> _p2pSettings;
    private readonly IOptionsMonitor<StorageSettings> _storageSettings;
    private readonly ILogger<PeerTracker> _logger;

    private readonly ConcurrentDictionary<string, PeerNodeRuntime> _nodesCache = new();
    //index of all known nodes
    private readonly ConcurrentDictionary<string, NodePersistentState> _nodeIndex = new();

    private int _isWritePending = 0;

    private string _baseDir = string.Empty;
    private string _currentIndexPath = string.Empty;
    private string _nodeVersionIndexPath = string.Empty;
    private int _newNodeLimit;
    private PeerTrackerIndex SavedIndex = new();

    private readonly IDisposable _settingsChangeListener;
    private readonly IDisposable _settingsChangeListener2;

    public event Action<PeerNodeRuntime>? OnPeerUpdate;
    private void InvokeOnPeerUpdate(PeerNodeRuntime time)
    {
        var handlers = OnPeerUpdate?.GetInvocationList();

        if (handlers != null)
        {
            foreach (Action<PeerNodeRuntime> handler in handlers)
            {
                _ = Task.Run(() =>
                {
                    try
                    {
                        handler(time);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Handshake audit handler failed.");
                    }
                });
            }
        }
    }
    public event Action<PeerNodeRuntime>? OnPeerNew;
    private void InvokeOnPeerNew(PeerNodeRuntime time)
    {
        var handlers = OnPeerNew?.GetInvocationList();

        if (handlers != null)
        {
            foreach (Action<PeerNodeRuntime> handler in handlers)
            {
                _ = Task.Run(() =>
                {
                    try
                    {
                        handler(time);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Handshake audit handler failed.");
                    }
                });
            }
        }
    }

    public PeerTracker(
        ILogger<PeerTracker> logger,
        IOptionsMonitor<P2PSettings> settings,
        IOptionsMonitor<StorageSettings> storageSettings,
        IConsensusStore archiveStore)
    {
        //_identity = nodeIdentity;
        _archiveStore = archiveStore;
        _logger = logger;

        _p2pSettings = settings;
        _storageSettings = storageSettings;

        UpdateSettings();
        _settingsChangeListener = _p2pSettings.OnChangeDebounced(_ => UpdateSettings());
        _settingsChangeListener2 = _storageSettings.OnChangeDebounced(_ => UpdateSettings());
    }

    private void UpdateSettings()
    {
        _baseDir = _p2pSettings.CurrentValue.DataStoragePath ?? "./Module_Data/P2P";
        _currentIndexPath = Path.Combine(_baseDir, "consensus_latest", "currentIndex");
        _nodeVersionIndexPath = Path.Combine(_baseDir, "consensus_latest", "nodeupdateindex");
        _newNodeLimit = _storageSettings.CurrentValue.MaxNewNodesDrip;
    }

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_currentIndexPath);
        Directory.CreateDirectory(_nodeVersionIndexPath);

        // Hydrate models out of disk allocations

        var savedIndexStatePath = Path.Combine(_baseDir, "consensus_latest", "currentIndex.json");
        if (File.Exists(savedIndexStatePath))
        {
            try
            {
                var index = JsonSerializer.Deserialize<PeerTrackerIndex>(
                    await File.ReadAllTextAsync(savedIndexStatePath)
                    );
                if (index != null)
                {
                    SavedIndex = index;
                }
            }
            catch
            {
            }

        }

        foreach (string file in Directory.GetFiles(_nodeVersionIndexPath, "*.json"))
        {
            try
            {
                var state = JsonSerializer.Deserialize<NodePersistentState>(await File.ReadAllTextAsync(file));
                if (state != null)
                {
                    _nodeIndex[state.NodeId] = state;
                }
            }
            catch { }
        }

        foreach (string file in Directory.GetFiles(_currentIndexPath, "*.json"))
        {
            try
            {
                var peer = JsonSerializer.Deserialize<PeerNodeHistoric>(await File.ReadAllTextAsync(file));
                if (peer != null)
                {
                    var peerRuntime = new PeerNodeRuntime(peer);
                    _nodesCache[peer.Identity.NodeId] = peerRuntime;
                }
            }
            catch { }
        }
    }

    public IEnumerable<PeerNodeHistoric> GetLastSeenNodes(Comparison<PeerNodeHistoric> sortingRule)
    {
        //var nodeStaleness = DateTime.UtcNow.AddDays(-1);
        var list = _nodesCache.Values.ToList();
        list.Sort(sortingRule);
        return list;
    }

    //return full list of available nodes
    public IEnumerable<PeerNodeHistoric> GetAvailableNodes(Comparison<PeerNodeHistoric> sortingRule)
    {
        var list = _nodeIndex.Values.ToList();
        List<PeerNodeHistoric> retList = new();
        foreach (var node in list)
        {
            if (_nodesCache.TryGetValue(node.NodeId, out var peerNode))
            {
                retList.Add(peerNode);
                continue;
            }
            else
            {
                var hist = _archiveStore.GetHistoricalPeer(node.LastIndexEpoch, node.NodeId);
                if (hist != null)
                {
                    retList.Add(hist);
                }
            }
        }

        retList.Sort(sortingRule);
        return retList;
    }


    public PeerNodeRuntime? GetPeer(string nodeId, bool historic = true)
    {
        if (_nodesCache.TryGetValue(nodeId, out var cached))
        {
            if (_nodeIndex.TryGetValue(nodeId, out var indexState))
            {
                cached.TrustScore = indexState.TrustScore;
            }
            return cached;
        }

        var loaded = LoadPeerFromDisk(nodeId, historic);
        if (loaded != null)
        {
            if (_nodeIndex.TryGetValue(nodeId, out var indexState))
            {
                loaded.TrustScore = indexState.TrustScore;
            }
            _nodesCache[nodeId] = loaded;
        }

        return loaded;
    }

    private PeerNodeRuntime? LoadPeerFromDisk(string nodeId, bool historic)
    {
        try
        {
            string today = DateTime.UtcNow.ToString("yyyy-MM-dd");
            var historicPeer = _archiveStore.GetHistoricalPeer(today, nodeId);
            if (historicPeer != null)
            {
                return new PeerNodeRuntime
                {
                    Identity = historicPeer.Identity,
                    TrustScore = historicPeer.TrustScore,
                    LastSeen = historicPeer.LastSeen,
                    IsVerified = historicPeer.IsVerified,
                    LastSeenByNodeId = historicPeer.LastSeenByNodeId
                };
            }
        }
        catch { }

        return null;
    }

    public NodePersistentState? GetPeerIndex(string nodeId) {

        if (_nodeIndex.TryGetValue(nodeId, out var index))
        {
            return index;
        }

        return null;
    }

    public void RemovePeer(string nodeId)
    {
        // Delete node files completely from memory and index and from file system
        // does not touch consensus storage, this has to be cleaned manually
        string nodePath = Path.Combine(_currentIndexPath, $"{nodeId}.json");
        string clockPath = Path.Combine(_nodeVersionIndexPath, $"{nodeId}.json");

        try
        {
            _nodeIndex.TryRemove(nodeId, out _);
            _nodesCache.TryRemove(nodeId, out _);

            if (File.Exists(clockPath))
            {
                File.Delete(clockPath);
            }
            if (File.Exists(nodePath))
            {
                File.Delete(nodePath);
            }

            _logger.LogInformation("[Peer Tracker] Cleaned up zero-epoch peer {NodeId} completely from cache and storage partitions.", nodeId);

        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Peer Tracker] Failed to write pruned peer state for node: {NodeId}", nodeId);
        }
    }

    public bool IsPeerExists(string nodeId)
    {
        return _nodesCache.ContainsKey(nodeId) ? true : _nodeIndex.ContainsKey(nodeId);
    }

    public Task UpdatePeerAsync(PeerImmutableData identity)
    {
        if (identity == null || string.IsNullOrEmpty(identity.NodeId)) return Task.CompletedTask;

        bool identityChanged = false;
        if (_nodesCache.TryGetValue(identity.NodeId, out var cache))
        {

            if (identity.Signature != null && cache.Identity.Signature != null &&
                !identity.Signature.SequenceEqual(cache.Identity.Signature) &&
                identity.SignatureTime > cache.Identity.SignatureTime)
            {
                identityChanged = true;
                cache.Identity = identity;
            }
        }
        else
        {
            cache = new PeerNodeRuntime() {
                Identity = identity
            };
            _nodesCache[identity.NodeId] = cache;
        }

        cache.LastSeen = DateTime.UtcNow;

        if (!_nodeIndex.TryGetValue(identity.NodeId, out var cacheIndex))
        {
            cacheIndex = new NodePersistentState
            {
                NodeId = identity.NodeId,
                LastUpdate = DateTime.UtcNow
            };

            _nodeIndex[identity.NodeId] = cacheIndex;
            InvokeOnPeerNew(cache);
        }
        else
        if (identityChanged)
        {
            cacheIndex.LastUpdate = DateTime.UtcNow;
            InvokeOnPeerUpdate(cache);
        }

        cache.TrustScore = cacheIndex.TrustScore;

        if (cacheIndex.LastIndexEpoch != SavedIndex?.TodayIndex || identityChanged)
        {
            cacheIndex.LastIndexEpoch = SavedIndex?.TodayIndex ?? DateTime.UtcNow.ToString("yyyy-MM-dd");

            _archiveStore.UpdateHistoricalPeer(cacheIndex.LastIndexEpoch, identity.NodeId, cache);
        }

        Volatile.Write(ref _isWritePending, 1);
        return Task.CompletedTask;
    }

    public Task UpdatePeerAsync(PeerNodeRuntime node)
    {
        if (node == null || string.IsNullOrEmpty(node.Identity.NodeId)) return Task.CompletedTask;

        var identity = node.Identity;

        bool identityChanged = false;
        if (_nodesCache.TryGetValue(identity.NodeId, out var cache))
        {
            // Protection Gate: Unverified incoming records cannot overwrite an already verified peer
            if (cache.IsVerified && !node.IsVerified)
            {
                _logger.LogWarning("[Peer Tracker Guard] Blocked unverified update attempt for verified Node {NodeId}", identity.NodeId);
                return Task.CompletedTask;
            }

            if (identity.Signature != null && cache.Identity.Signature != null &&
                !identity.Signature.SequenceEqual(cache.Identity.Signature) &&
                identity.SignatureTime > cache.Identity.SignatureTime) // we are not intrested in old identities
            {
                identityChanged = true;
            }
        }

        cache = node;
        _nodesCache[identity.NodeId] = cache;

        if (!_nodeIndex.TryGetValue(identity.NodeId, out var cacheIndex))
        {
            cacheIndex = new NodePersistentState
            {
                NodeId = identity.NodeId,
                LastUpdate = DateTime.UtcNow
            };

            _nodeIndex[identity.NodeId] = cacheIndex;
            InvokeOnPeerNew(cache);
        }
        else
        if (identityChanged)
        {
            InvokeOnPeerUpdate(cache);
        }

        cacheIndex.LastUpdate = DateTime.UtcNow;
        cacheIndex.TrustScore = cache.TrustScore;

        if (cacheIndex.LastIndexEpoch != SavedIndex?.TodayIndex || identityChanged)
        {
            //only update respective index epoch
            cacheIndex.LastIndexEpoch = node.Identity.SignatureTime.ToString("yyyy-MM-dd");

            _archiveStore.UpdateHistoricalPeer(cacheIndex.LastIndexEpoch, identity.NodeId, cache);
        }

        Volatile.Write(ref _isWritePending, 1);
        return Task.CompletedTask;
    }

    public async Task UpdatePeerWasSeenByAsync(string nodeId, string seenById)
    {
        var peer = GetPeer(nodeId, false);
        peer?.LastSeenByNodeId.Add(seenById);
    }

    public async Task UpdatePeerWasNotSeenByAsync(string nodeId, string notSeenBy)
    {
        var peer = GetPeer(nodeId, false);
        peer?.LastSeenByNodeId.Remove(notSeenBy);
    }

    public async Task UpdatePeerActivityAsync(string nodeId)
    {
        if (_nodesCache.TryGetValue(nodeId, out var cache))
        {
            cache.LastSeen = DateTime.UtcNow;
        }

        if (_nodeIndex.TryGetValue(nodeId, out var cacheIndex))
        {
            cacheIndex.LastUpdate = DateTime.UtcNow;
        }
    }


    /// <summary>
    /// Directly updates and synchronizes absolute trust scores in memory across Node Persistent Index 
    /// and Peer Cache without performing direct disk writes.
    /// </summary>
    public Task UpdateTrustAsync(string nodeId, double absoluteTrustScore)
    {
        if (string.IsNullOrEmpty(nodeId)) return Task.CompletedTask;

        var nowUtc = DateTime.UtcNow;

        if (_nodeIndex.TryGetValue(nodeId, out var state))
        {
            state.TrustScore = absoluteTrustScore;
            state.LastUpdate = nowUtc;
        }
        else
        {
            _nodeIndex[nodeId] = new NodePersistentState
            {
                NodeId = nodeId,
                TrustScore = absoluteTrustScore,
                LastUpdate = nowUtc
            };
        }

        if (_nodesCache.TryGetValue(nodeId, out var runtimeNode))
        {
            runtimeNode.TrustScore = absoluteTrustScore;
        }
        else
        {
            var peerFromDisk = GetPeer(nodeId, historic: true);
            if (peerFromDisk != null)
            {
                peerFromDisk.TrustScore = absoluteTrustScore;
                _nodesCache[nodeId] = peerFromDisk;
            }
        }

        Volatile.Write(ref _isWritePending, 1);

        return Task.CompletedTask;
    }
    //public async Task UpdateTrustAsync(string nodeId, double amount)
    //{
    //    var peer = GetPeer(nodeId);
    //    if (peer != null)
    //    {
    //        peer.TrustScore = Math.Clamp(peer.TrustScore + amount, -50, 100);
    //        await UpdatePeerAsync(peer);
    //    }
    //}

    /// <summary>
    /// API Method implementation: Returns all historical consensus dates a given node was seen in.
    /// </summary>
    public List<string> GetRecordedEpochsForNode(string nodeId)
    {
        if (string.IsNullOrEmpty(nodeId)) return new List<string>();
        return _nodeIndex.TryGetValue(nodeId, out var state) ? state.IndexEpochs.ToList() : new List<string>();
    }

    /// <summary>
    /// API Method implementation: Returns the pointer to the most recent historical activity epoch.
    /// </summary>
    public string GetMostRecentEpochForNode(string nodeId)
    {
        if (string.IsNullOrEmpty(nodeId)) return string.Empty;
        return _nodeIndex.TryGetValue(nodeId, out var state) ? state.LastIndexEpoch : string.Empty;
    }

    #region Cache
    public async Task FlushPendingUpdatesToDiskAsync()
    {
        // Check volatile write state barrier immediately
        if (Interlocked.Exchange(ref _isWritePending, 0) == 0) return;

        //var nodesToGossip = new List<string>();
        SavedIndex.TodayIndexData = DateTime.UtcNow;
        SavedIndex.TodayIndex = DateTime.UtcNow.ToString("yyyy-MM-dd");

        int allowedNewNodesQuota = _newNodeLimit;
        int newNodesPersistedCount = 0;

        var nodesCache = _nodesCache.ToList();

        // Process all live memory index allocations
        foreach (var kvp in nodesCache)
        {
            string nodeId = kvp.Key;
            PeerNodeRuntime currentMemoryNode = kvp.Value; // Using PeerNodeRuntime from cache context

            if ((DateTime.UtcNow - currentMemoryNode.LastSeen).Days > 0)
            {
                _nodesCache.Remove(nodeId, out _);
                continue;
            }

            if (currentMemoryNode.Identity == null) continue;

            string todayStr = currentMemoryNode.Identity.SignatureTime.ToString("yyyy-MM-dd") ;

            string nodePath = Path.Combine(_currentIndexPath, $"{nodeId}.json");
            string clockPath = Path.Combine(_nodeVersionIndexPath, $"{nodeId}.json");

            PeerNodeHistoric? diskNode = null;

            // 1. Read baseline files from disk if they exist to serve as the previous version layer
            if (File.Exists(nodePath))
            {
                try
                {
                    string rawNodeJson = await File.ReadAllTextAsync(nodePath);
                    diskNode = JsonSerializer.Deserialize<PeerNodeHistoric>(rawNodeJson);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[Peer Tracker Engine] Corruption reading disk state for node: {NodeId}", nodeId);
                }
            }

            bool isNewNode = !File.Exists(clockPath);

            // Rate limit: If this node is completely new and we have exhausted our drip quota for this cycle, skip persistence completely.
            if (isNewNode && newNodesPersistedCount >= allowedNewNodesQuota)
            {
                // Re-arm write barrier flag to ensure dropped new nodes are captured on subsequent flush execution cycles
                Volatile.Write(ref _isWritePending, 1);
                continue;
            }

            NodePersistentState? diskClockState = null;
            _nodeIndex.TryGetValue(nodeId, out diskClockState);

            // Initialize missing structural wrappers for accepted tracks
            if (diskClockState == null)
            {
                diskClockState = new NodePersistentState
                {
                    NodeId = nodeId,
                    LastIndexEpoch = todayStr,
                };
                diskClockState.IndexEpochs.Add(todayStr);

                // Build temporary historical mapping required by downstream store sweeps
                _archiveStore.UpdateHistoricalPeer(todayStr, nodeId, currentMemoryNode);
                newNodesPersistedCount++;
            }

            if (diskNode == null)
            {
                diskNode = new PeerNodeHistoric
                {
                    Identity = currentMemoryNode.Identity,
                    TrustScore = currentMemoryNode.TrustScore
                };
                _archiveStore.UpdateHistoricalPeer(todayStr, nodeId, diskNode);
            }

            if (!diskClockState.IndexEpochs.Contains(todayStr))
            {
                diskClockState.IndexEpochs.Add(todayStr);
            }

            // Synchronize in-memory cache models with fully evaluated state schemas
            _nodeIndex[nodeId] = diskClockState;

            // 4. Push finalized payloads down onto raw storage file partitions
            await File.WriteAllTextAsync(nodePath, JsonSerializer.Serialize(currentMemoryNode, new JsonSerializerOptions { WriteIndented = true }));
            await File.WriteAllTextAsync(clockPath, JsonSerializer.Serialize(diskClockState, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    public void PruneHistory()
    {
        int daysToKeep = _storageSettings.CurrentValue.RetainedHistoryEpochsCount;
        if (daysToKeep <= 0) daysToKeep = 30; // Global fallback count limit

        int nodeDaysToKeep = _storageSettings.CurrentValue.RetainedNodeHistoryEpochs;
        if (nodeDaysToKeep <= 0) nodeDaysToKeep = 7; // Per-node floor bound

        DateTime cutoffDate = DateTime.UtcNow.Date.AddDays(-daysToKeep);
        _logger.LogInformation("[Peer Tracker] Initiating prioritized history pruning sweep. Global epoch retention limit: {Days} days. Node floor history size limit: {NodeDays} epochs.",
            daysToKeep, nodeDaysToKeep);

        foreach (var kvp in _nodeIndex)
        {
            string nodeId = kvp.Key;
            NodePersistentState state = kvp.Value;

            if (state == null || state.IndexEpochs == null) continue;

            // 1. Sort existing epochs descending (newest first) to establish precise priorities
            var sortedEpochs = state.IndexEpochs
                .Select(epoch => {
                    bool valid = DateTime.TryParse(epoch, out var parsedDate);
                    return new { Raw = epoch, Date = parsedDate, IsValid = valid };
                })
                .OrderByDescending(x => x.IsValid ? x.Date : DateTime.MinValue)
                .ToList();

            var remainingEpochs = new List<string>();

            // 2. Prioritize nodeDaysToKeep (leave at least this amount of history) over the global daysToKeep cutoff
            for (int i = 0; i < sortedEpochs.Count; i++)
            {
                var item = sortedEpochs[i];
                if (i < nodeDaysToKeep || (item.IsValid && item.Date >= cutoffDate))
                {
                    remainingEpochs.Add(item.Raw);
                }
            }

            // 3. Re-sort remaining epochs ascending to match standard chronological order
            remainingEpochs = remainingEpochs
                .Select(epoch => {
                    bool valid = DateTime.TryParse(epoch, out var parsedDate);
                    return new { Raw = epoch, Date = parsedDate, IsValid = valid };
                })
                .OrderBy(x => x.IsValid ? x.Date : DateTime.MinValue)
                .Select(x => x.Raw)
                .Distinct()
                .ToList();

            bool changed = state.IndexEpochs.Count != remainingEpochs.Count ||
                           !state.IndexEpochs.SequenceEqual(remainingEpochs);

            if (changed)
            {
                state.IndexEpochs.UnionWith(remainingEpochs);
                state.LastIndexEpoch = remainingEpochs.LastOrDefault() ?? string.Empty;

                string nodePath = Path.Combine(_currentIndexPath, $"{nodeId}.json");
                string clockPath = Path.Combine(_nodeVersionIndexPath, $"{nodeId}.json");

                try
                {
                    if (state.IndexEpochs.Count == 0)
                    {
                        // Delete node files completely from indices and cache if it has zero active epochs left
                        _nodeIndex.TryRemove(nodeId, out _);
                        _nodesCache.TryRemove(nodeId, out _);

                        if (File.Exists(clockPath))
                        {
                            File.Delete(clockPath);
                        }
                        if (File.Exists(nodePath))
                        {
                            File.Delete(nodePath);
                        }

                        _logger.LogInformation("[Peer Tracker] Cleaned up zero-epoch peer {NodeId} completely from cache and storage partitions.", nodeId);
                    }
                    else
                    {
                        string json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
                        File.WriteAllText(clockPath, json);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[Peer Tracker] Failed to write pruned peer state for node: {NodeId}", nodeId);
                }
            }
        }

        // Call the ConsensusStore asynchronous file system pruning engine
        Task.Run(async () =>
        {
            try
            {
                await _archiveStore.PruneHistoryAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Peer Tracker] Failed executing ConsensusStore async disk pruner task pipeline.");
            }
        });
    }

    #endregion

    /// <summary>
    /// Preferred shutdown path: the host disposes singletons asynchronously, so the final flush is
    /// awaited rather than blocked on.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        _settingsChangeListener?.Dispose();
        _settingsChangeListener2?.Dispose();
        await FlushPendingUpdatesToDiskAsync();
    }

    /// <summary>Synchronous fallback for callers that can only dispose synchronously.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        _settingsChangeListener?.Dispose();
        _settingsChangeListener2?.Dispose();
        FlushPendingUpdatesToDiskAsync().GetAwaiter().GetResult();
    }

    private int _disposed;

}