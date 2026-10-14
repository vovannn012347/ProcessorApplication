
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

using FileTransferModule.Configuration;
using FileTransferModule.Models;
using FileTransferModule.Models.Torrent;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using P2PModule.Interfaces;
using P2PModule.Services.Connection;
using P2PModule.Services.Persistence;

using SIPSorcery.Net;

namespace FileTransferModule.Services;

/// <summary>
/// Client-side multi-source download coordinator.
/// Requests block slices from parallel peers, verifies them via StorageEngine, and finalizes file assembly.
/// </summary>
public interface ISwarmDownloadCoordinator
{
    /// <summary>
    /// Starts a new download task based on the provided manifest.
    /// </summary>
    Task<string> StartDownloadAsync(
        PTorrentManifest manifest,
        string? customTargetDirectory = null,
        string? customFileName = null,
        bool isLocalAdmin = false);

    /// <summary>
    /// Pauses an active download task.
    /// </summary>
    Task PauseDownloadAsync(string taskId);

    /// <summary>
    /// Resumes a paused download task.
    /// </summary>
    Task ResumeDownloadAsync(string taskId);

    /// <summary>
    /// Cancels a download task and cleans up temporary staging files.
    /// </summary>
    Task CancelDownloadAsync(string taskId);

    /// <summary>
    /// Retrieves current state and telemetry for a specific task.
    /// </summary>
    SwarmTransferTaskState? GetTaskState(string taskId);

    /// <summary>
    /// Retrieves all active and completed download tasks.
    /// </summary>
    List<SwarmTransferTaskState> GetAllTasks();

    /// <summary>Node-wide download rate / last minute / total (accepted slices, all tasks).</summary>
    TransferDirectionStats GetTotalDownloadStats();

    /// <summary>Download tasks currently running (not queued/paused/finished).</summary>
    int RunningDownloads { get; }
}


public class SwarmDownloadCoordinator : ISwarmDownloadCoordinator, IDisposable
{
    private readonly IPeerConnectionManager _connectionManager;
    private readonly IFileChunkStorageEngine _storageEngine;
    private readonly ITorrentWireHandler _wireHandler;
    private readonly IFileTrackerCoordinator _trackerCoordinator;
    private readonly IFileTrackerAnnounceService _trackerAnnounce;
    private readonly IP2PRouterService _router;
    private readonly IFileCatalogConsensusChannel _catalogChannel;
    private readonly IFileArchiveService _archiveService;
    private readonly IFilePeerReputationService _reputation;
    private readonly ISwarmOrchestratorService _orchestrator;
    private readonly IPeerTracker _peerTracker;
    private readonly INodeIdentityService _identity;
    private readonly IOptionsMonitor<FileSharingSettings> _settings;
    private readonly ILogger<SwarmDownloadCoordinator> _logger;

    private readonly ConcurrentDictionary<string, DownloadSession> _activeDownloads = new();

    /// <summary>How long after opening a channel we wait for the seeder's BITFIELD before assuming it has everything (older seeders).</summary>
    private static readonly TimeSpan BitfieldGracePeriod = TimeSpan.FromSeconds(3);

    /// <summary>A channel still "connecting" after this long is treated as dead.</summary>
    private static readonly TimeSpan ChannelOpenTimeout = TimeSpan.FromSeconds(20);

    /// <summary>A peer that advertised nothing / went away is not retried by rediscovery for this long.</summary>
    private static readonly TimeSpan PeerCooldown = TimeSpan.FromMinutes(2);

    private const int MaxAttemptsPerBlock = 10;

    private readonly record struct SliceKey(int Block, int Slice);

    private sealed class SliceRequest
    {
        public string NodeId { get; init; } = string.Empty;
        public DateTime SentUtc { get; init; }
    }

    /// <summary>One open (or opening) file channel to one seeder within one download session.</summary>
    private sealed class PeerLink
    {
        public string NodeId { get; init; } = string.Empty;
        public RTCDataChannel Channel { get; init; } = null!;
        public DateTime CreatedUtc { get; } = DateTime.UtcNow;

        /// <summary>What the seeder told us it has (BITFIELD + HAVE_BLOCK). Null until its BITFIELD arrives.</summary>
        public volatile SwarmBitfield? RemoteHave;

        /// <summary>Requests sent to this peer and not yet answered/expired. Updated with Interlocked.</summary>
        public int InFlight;

        public volatile bool Dropped;

        private DateTime? _openedUtc;

        /// <summary>Open - requests can be sent.</summary>
        public bool IsUsable => !Dropped && Channel.readyState == RTCDataChannelState.open;

        /// <summary>Open, or still opening within ChannelOpenTimeout - don't replace it or rediscover around it.</summary>
        public bool IsAlive => IsUsable ||
            (!Dropped && Channel.readyState == RTCDataChannelState.connecting && DateTime.UtcNow - CreatedUtc < ChannelOpenTimeout);

        public bool MayHave(int blockIndex)
        {
            var have = RemoteHave;
            if (have != null) return have.HasBlock(blockIndex);
            if (Channel.readyState != RTCDataChannelState.open) return false;

            // No BITFIELD yet: give the seeder a moment after the channel opens to send one, then
            // assume a full seeder (seeders that predate BITFIELD handling only serve complete files).
            _openedUtc ??= DateTime.UtcNow;
            return DateTime.UtcNow - _openedUtc.Value > BitfieldGracePeriod;
        }
    }

    private class DownloadSession
    {
        public string TaskId { get; set; } = string.Empty;
        public PTorrentManifest Manifest { get; set; } = new();
        public SwarmTransferTaskState State { get; set; } = new();
        public string? CustomTargetDirectory { get; set; }
        public string? CustomFileName { get; set; }
        public bool IsLocalAdmin { get; set; }
        public CancellationTokenSource Cts { get; set; } = new();

        /// <summary>True while the pipeline task runs; counts against MaxActiveDownloads.</summary>
        public volatile bool Running;

        /// <summary>FIFO position for the download queue (assigned on start and on resume).</summary>
        public long QueueOrder;

        /// <summary>Bytes of accepted slices - read only when the UI asks for the task's speed.</summary>
        public TransferRateMeter DownloadMeter { get; } = new();

        // BlockIndex -> SliceIndex -> isReceived (staged on disk, not yet verified)
        public ConcurrentDictionary<int, ConcurrentDictionary<int, bool>> PendingSlices { get; } = new();

        public ConcurrentDictionary<string, PeerLink> Links { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Every slice request currently awaiting an answer, and who it was sent to.</summary>
        public ConcurrentDictionary<SliceKey, SliceRequest> Outstanding { get; } = new();

        /// <summary>Who delivered each staged slice and how many bytes - used to attribute a block's verification result.</summary>
        public ConcurrentDictionary<SliceKey, (string NodeId, int Bytes)> SliceSources { get; } = new();

        /// <summary>Block -> the single peer it must be fetched from (isolation retry after a mixed-source hash failure).</summary>
        public ConcurrentDictionary<int, string> ForcedSource { get; } = new();

        /// <summary>Block -> peers already tried as sole source for it.</summary>
        public ConcurrentDictionary<int, ConcurrentDictionary<string, byte>> TriedSoleSources { get; } = new();

        /// <summary>Peers caught sending corrupt data in this session - never reconnected for this task.</summary>
        public ConcurrentDictionary<string, byte> Excluded { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Peers temporarily skipped by rediscovery (advertised no blocks, channel failed...).</summary>
        public ConcurrentDictionary<string, DateTime> CooldownUntil { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Session-local count of mixed-source failures per peer - the most suspicious peer is isolated first.</summary>
        public ConcurrentDictionary<string, int> Suspicion { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public SwarmDownloadCoordinator(
        IPeerConnectionManager connectionManager,
        IFileChunkStorageEngine storageEngine,
        ITorrentWireHandler wireHandler,
        IFileTrackerCoordinator trackerCoordinator,
        IFileTrackerAnnounceService trackerAnnounce,
        IP2PRouterService router,
        IFileCatalogConsensusChannel catalogChannel,
        IFileArchiveService archiveService,
        IFilePeerReputationService reputation,
        ISwarmOrchestratorService orchestrator,
        IPeerTracker peerTracker,
        INodeIdentityService identity,
        IOptionsMonitor<FileSharingSettings> settings,
        ILogger<SwarmDownloadCoordinator> logger)
    {
        _connectionManager = connectionManager;
        _storageEngine = storageEngine;
        _wireHandler = wireHandler;
        _trackerCoordinator = trackerCoordinator;
        _trackerAnnounce = trackerAnnounce;
        _router = router;
        _catalogChannel = catalogChannel;
        _archiveService = archiveService;
        _reputation = reputation;
        _orchestrator = orchestrator;
        _peerTracker = peerTracker;
        _identity = identity;
        _settings = settings;
        _logger = logger;
    }

    public async Task<string> StartDownloadAsync(
        PTorrentManifest manifest,
        string? customTargetDirectory = null,
        string? customFileName = null,
        bool isLocalAdmin = false)
    {
        string taskId = Guid.NewGuid().ToString("N");

        var session = new DownloadSession
        {
            TaskId = taskId,
            Manifest = manifest,
            CustomTargetDirectory = customTargetDirectory,
            CustomFileName = customFileName,
            IsLocalAdmin = isLocalAdmin,
            State = new SwarmTransferTaskState
            {
                TaskId = taskId,
                ContentHash = manifest.ContentHash,
                FileName = manifest.FileName,
                TotalSizeBytes = manifest.TotalSizeBytes,
                Bitfield = new SwarmBitfield(manifest.TotalBlocksCount),
                Status = SwarmTaskStatus.Queued,
                StartedUtc = DateTime.UtcNow
            },
            QueueOrder = Interlocked.Increment(ref _queueSequence)
        };

        await _storageEngine.InitializeTaskStorageAsync(taskId, manifest);
        _activeDownloads[taskId] = session;

        _logger.LogInformation("[DownloadCoordinator] Enqueued download task {TaskId} for file {Name}", taskId, manifest.FileName);
        StartQueuedDownloads();
        return taskId;
    }

    #region Download queue

    private long _queueSequence;
    private readonly TransferRateMeter _totalDownloadMeter = new();

    public TransferDirectionStats GetTotalDownloadStats() => _totalDownloadMeter.Snapshot();

    public int RunningDownloads => _activeDownloads.Values.Count(s => s.Running);
    private readonly object _queueLock = new();

    /// <summary>
    /// Starts queued tasks (oldest first) while fewer than MaxActiveDownloads are running. Called
    /// when a task is added or resumed, and whenever a running pipeline ends for any reason
    /// (complete, failed, paused, cancelled) - so a freed slot is refilled immediately.
    /// </summary>
    private void StartQueuedDownloads()
    {
        var toStart = new List<DownloadSession>();
        lock (_queueLock)
        {
            int max = Math.Max(1, _settings.CurrentValue.MaxActiveDownloads);
            int running = _activeDownloads.Values.Count(s => s.Running);

            foreach (var session in _activeDownloads.Values
                         .Where(s => !s.Running && s.State.Status == SwarmTaskStatus.Queued)
                         .OrderBy(s => s.QueueOrder))
            {
                if (running >= max) break;
                session.Running = true;
                session.State.Status = SwarmTaskStatus.Staging;
                running++;
                toStart.Add(session);
            }
        }

        foreach (var session in toStart)
        {
            var s = session;
            _ = Task.Run(async () =>
            {
                try
                {
                    await ExecuteDownloadPipelineAsync(s);
                }
                finally
                {
                    s.Running = false;
                    StartQueuedDownloads();
                }
            });
        }
    }

    #endregion

    private async Task ExecuteDownloadPipelineAsync(DownloadSession session)
    {
        var token = session.Cts.Token;
        try
        {
            // Fresh start or resume after pause: anything that was in flight is gone.
            ResetInFlightState(session);

            // 1. Discover seeders and connect (manifest initial peers + trackers + catalog consensus),
            //    best file-reputation first, capped at MaxPeerConnectionsPerFile.
            var seeders = await DiscoverSeedersAsync(session.Manifest);
            await ConnectToSeedersAsync(session, seeders, token);

            // 2. Block loop. Each block gets up to MaxAttemptsPerBlock tries. A block that fails
            //    SHA-256 verification is discarded and downloaded again - nobody's P2P rating is
            //    touched; the outcome only feeds the separate file-source reputation.
            for (int blockIndex = 0; blockIndex < session.Manifest.TotalBlocksCount; blockIndex++)
            {
                if (token.IsCancellationRequested) break;
                if (session.State.Bitfield.HasBlock(blockIndex)) continue;

                bool blockCompleted = false;
                int attempt = 0;

                while (!blockCompleted && attempt < MaxAttemptsPerBlock && !token.IsCancellationRequested)
                {
                    attempt++;

                    if (!session.Links.Values.Any(l => l.IsAlive))
                    {
                        int found = await RediscoverAndConnectSeedersAsync(session, token);
                        if (found == 0)
                        {
                            // Nothing available right now - back off rather than hammering discovery.
                            await Task.Delay(TimeSpan.FromSeconds(10), token);
                            continue;
                        }
                    }

                    bool allSlicesStaged = await DownloadBlockAsync(session, blockIndex, token);
                    if (!allSlicesStaged)
                    {
                        _logger.LogWarning("[DownloadCoordinator] Task {TaskId}: block {BlockIndex} attempt {Attempt}/{Max} stalled.",
                            session.TaskId, blockIndex, attempt, MaxAttemptsPerBlock);
                        continue;
                    }

                    bool verified = await _storageEngine.VerifyAndCommitBlockAsync(session.TaskId, session.Manifest, blockIndex);
                    if (verified)
                    {
                        OnBlockVerified(session, blockIndex);
                        blockCompleted = true;
                    }
                    else
                    {
                        // Storage engine already deleted the staged slices; forget them here too so
                        // the next attempt actually re-requests them (previously they stayed marked
                        // as received, so every retry requested nothing and the block could never pass).
                        OnBlockCorrupt(session, blockIndex);
                    }
                }

                if (!blockCompleted && !token.IsCancellationRequested)
                {
                    _logger.LogError("[DownloadCoordinator] Task {TaskId}: giving up on block {BlockIndex} after {Attempts} attempts.",
                        session.TaskId, blockIndex, attempt);
                    session.State.Status = SwarmTaskStatus.Failed;
                    session.State.ErrorMessage = $"Unable to download block {blockIndex} after {attempt} attempts - no seeder delivered a valid copy.";
                    CloseAllLinks(session);
                    return;
                }
            }

            // 3. Final assembly and commit
            if (session.State.Bitfield.IsComplete())
            {
                session.State.Status = SwarmTaskStatus.Assembling;
                string? finalPath = await _storageEngine.FinalizeFileAssemblyAsync(
                    session.TaskId,
                    session.Manifest,
                    session.CustomTargetDirectory,
                    session.CustomFileName,
                    session.IsLocalAdmin);

                session.State.TargetSavePath = finalPath ?? string.Empty;
                session.State.Status = SwarmTaskStatus.Complete;
                session.State.CompletedUtc = DateTime.UtcNow;
                CloseAllLinks(session);

                _logger.LogInformation("[DownloadCoordinator] Successfully completed task {TaskId}. Output: {Path}", session.TaskId, finalPath);

                // 4. Leecher -> seeder: make the finished file available to the swarm.
                await TrySeedCompletedDownloadAsync(session, finalPath);
            }
        }
        catch (OperationCanceledException)
        {
            session.State.Status = SwarmTaskStatus.Paused;
            _logger.LogInformation("[DownloadCoordinator] Task {TaskId} paused/cancelled.", session.TaskId);
        }
        catch (Exception ex)
        {
            session.State.Status = SwarmTaskStatus.Failed;
            session.State.ErrorMessage = ex.Message;
            _logger.LogError(ex, "[DownloadCoordinator] Exception in task {TaskId}", session.TaskId);
        }
    }

    #region Block scheduling

    /// <summary>
    /// Requests every missing slice of one block, spreading requests over usable peers that have
    /// the block, with at most MaxOutstandingRequestsPerPeer in flight per peer. Unanswered
    /// requests expire after SliceRequestTimeoutSeconds and are re-assigned. Returns true once all
    /// slices are staged on disk, false if no progress was made for two timeout periods.
    /// </summary>
    private async Task<bool> DownloadBlockAsync(DownloadSession session, int blockIndex, CancellationToken token)
    {
        int sliceCount = session.Manifest.GetSlicesCountForBlock(blockIndex);
        var staged = session.PendingSlices.GetOrAdd(blockIndex, _ => new ConcurrentDictionary<int, bool>());

        var sliceTimeout = TimeSpan.FromSeconds(Math.Max(1, _settings.CurrentValue.SliceRequestTimeoutSeconds));
        var stallLimit = sliceTimeout * 2;
        int window = Math.Max(1, _settings.CurrentValue.MaxOutstandingRequestsPerPeer);

        int lastDone = -1;
        var lastProgressUtc = DateTime.UtcNow;
        int roundRobin = 0;

        while (!token.IsCancellationRequested)
        {
            int done = staged.Count(kv => kv.Value);
            if (done >= sliceCount) return true;

            var now = DateTime.UtcNow;
            if (done != lastDone) { lastDone = done; lastProgressUtc = now; }
            else if (now - lastProgressUtc > stallLimit) return false;

            ExpireTimedOutRequests(session, blockIndex, sliceTimeout);

            var candidates = GetCandidatesForBlock(session, blockIndex);
            if (candidates.Count == 0)
            {
                // No live channel at all -> let the outer loop rediscover now. Channels still opening,
                // or open but without this block (yet) -> wait; the stall limit bounds this.
                if (!session.Links.Values.Any(l => l.IsAlive)) return false;
                await Task.Delay(250, token);
                continue;
            }

            for (int sliceIndex = 0; sliceIndex < sliceCount; sliceIndex++)
            {
                if (staged.TryGetValue(sliceIndex, out bool isDone) && isDone) continue;

                var key = new SliceKey(blockIndex, sliceIndex);
                if (session.Outstanding.ContainsKey(key)) continue;

                PeerLink? target = null;
                for (int k = 0; k < candidates.Count; k++)
                {
                    var c = candidates[(roundRobin + k) % candidates.Count];
                    if (c.IsUsable && Volatile.Read(ref c.InFlight) < window)
                    {
                        target = c;
                        roundRobin = (roundRobin + k + 1) % candidates.Count;
                        break;
                    }
                }
                if (target == null) break; // every candidate's window is full - wait for answers

                var request = new SliceRequest { NodeId = target.NodeId, SentUtc = DateTime.UtcNow };
                if (!session.Outstanding.TryAdd(key, request))
                    continue;

                Interlocked.Increment(ref target.InFlight);
                try
                {
                    target.Channel.send(_wireHandler.BuildRequestSliceFrame(blockIndex, sliceIndex));
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "[DownloadCoordinator] Send to {Peer} failed; dropping link.", target.NodeId);
                    if (session.Outstanding.TryRemove(new KeyValuePair<SliceKey, SliceRequest>(key, request)))
                    {
                        Interlocked.Decrement(ref target.InFlight);
                    }
                    DropLink(session, target, "send failed", excludeForSession: false);
                }
            }

            await Task.Delay(20, token);
        }

        return false;
    }

    /// <summary>
    /// Usable peers that have (or may have) the block, best file reputation first. When the block
    /// is pinned to a single source for isolation, only that peer - unless it is gone, in which case
    /// the pin is dropped and the block goes back to normal multi-source fetching.
    /// </summary>
    private List<PeerLink> GetCandidatesForBlock(DownloadSession session, int blockIndex)
    {
        var usable = new List<PeerLink>();
        foreach (var link in session.Links.Values)
        {
            if (!link.IsUsable) continue;
            if (_reputation.IsBanned(link.NodeId))
            {
                DropLink(session, link, "banned as a file source", excludeForSession: true);
                continue;
            }
            if (link.MayHave(blockIndex)) usable.Add(link);
        }

        if (session.ForcedSource.TryGetValue(blockIndex, out var forcedNodeId))
        {
            var forced = usable.FirstOrDefault(l => string.Equals(l.NodeId, forcedNodeId, StringComparison.OrdinalIgnoreCase));
            if (forced != null) return new List<PeerLink> { forced };

            session.ForcedSource.TryRemove(blockIndex, out _);
            _logger.LogInformation("[DownloadCoordinator] Task {TaskId}: isolation source {Peer} for block {BlockIndex} unavailable - back to multi-source.",
                session.TaskId, forcedNodeId, blockIndex);
        }

        return usable
            .OrderByDescending(l => _reputation.GetScore(l.NodeId))
            .ThenBy(l => Volatile.Read(ref l.InFlight))
            .ToList();
    }

    private void ExpireTimedOutRequests(DownloadSession session, int blockIndex, TimeSpan timeout)
    {
        var cutoff = DateTime.UtcNow - timeout;
        foreach (var kv in session.Outstanding)
        {
            if (kv.Key.Block != blockIndex || kv.Value.SentUtc > cutoff) continue;
            if (!session.Outstanding.TryRemove(kv)) continue;

            if (session.Links.TryGetValue(kv.Value.NodeId, out var link))
            {
                Interlocked.Decrement(ref link.InFlight);
            }
            _reputation.RecordSliceTimeout(kv.Value.NodeId);
        }
    }

    private void OnBlockVerified(DownloadSession session, int blockIndex)
    {
        session.State.Bitfield.SetBlock(blockIndex, true);

        // Credit every contributor with the bytes it supplied to this now-verified block.
        foreach (var contribution in TakeSliceSources(session, blockIndex).GroupBy(s => s.NodeId, StringComparer.OrdinalIgnoreCase))
        {
            _reputation.RecordVerifiedContribution(contribution.Key, contribution.Sum(s => (long)s.Bytes));
        }

        session.PendingSlices.TryRemove(blockIndex, out _);
        session.ForcedSource.TryRemove(blockIndex, out _);
        session.TriedSoleSources.TryRemove(blockIndex, out _);

        // Broadcast HAVE_BLOCK (0x04)
        byte[] haveFrame = _wireHandler.BuildHaveBlockFrame(blockIndex);
        foreach (var link in session.Links.Values)
        {
            if (!link.IsUsable) continue;
            try { link.Channel.send(haveFrame); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// A block failed SHA-256. Discard it and decide how to re-fetch:
    ///  - one contributor: the corrupt data is provably theirs -> record it (may ban) and drop them
    ///    from this download;
    ///  - several contributors: record a shared-source failure for each, then pin the block to a
    ///    single peer (most suspicious first) so the next failure, if any, is attributable.
    /// </summary>
    private void OnBlockCorrupt(DownloadSession session, int blockIndex)
    {
        var contributors = TakeSliceSources(session, blockIndex)
            .Select(s => s.NodeId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (session.PendingSlices.TryGetValue(blockIndex, out var staged)) staged.Clear();

        if (contributors.Count == 1)
        {
            string culprit = contributors[0];
            _logger.LogWarning("[DownloadCoordinator] Task {TaskId}: block {BlockIndex} failed verification; sole source {Peer} - discarding and dropping that peer for this download.",
                session.TaskId, blockIndex, culprit);

            _reputation.RecordBadBlock(culprit, soleSource: true);
            session.ForcedSource.TryRemove(blockIndex, out _);
            if (session.Links.TryGetValue(culprit, out var link))
            {
                DropLink(session, link, "served a corrupt block", excludeForSession: true);
            }
            else
            {
                session.Excluded[culprit] = 1;
            }
            return;
        }

        foreach (var nodeId in contributors)
        {
            _reputation.RecordBadBlock(nodeId, soleSource: false);
            session.Suspicion.AddOrUpdate(nodeId, 1, (_, n) => n + 1);
        }

        var tried = session.TriedSoleSources.GetOrAdd(blockIndex, _ => new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase));
        var isolate = session.Links.Values
            .Where(l => l.IsUsable && l.MayHave(blockIndex) && !tried.ContainsKey(l.NodeId))
            .OrderByDescending(l => contributors.Contains(l.NodeId, StringComparer.OrdinalIgnoreCase)) // suspects first
            .ThenByDescending(l => session.Suspicion.GetValueOrDefault(l.NodeId))
            .ThenBy(l => _reputation.GetScore(l.NodeId))
            .FirstOrDefault();

        if (isolate != null)
        {
            tried[isolate.NodeId] = 1;
            session.ForcedSource[blockIndex] = isolate.NodeId;
            _logger.LogWarning("[DownloadCoordinator] Task {TaskId}: block {BlockIndex} failed verification with {Count} contributors - discarding and re-fetching it from {Peer} alone to isolate the bad source.",
                session.TaskId, blockIndex, contributors.Count, isolate.NodeId);
        }
        else
        {
            session.ForcedSource.TryRemove(blockIndex, out _);
            _logger.LogWarning("[DownloadCoordinator] Task {TaskId}: block {BlockIndex} failed verification with {Count} contributors - discarding and re-fetching (no untried single source left).",
                session.TaskId, blockIndex, contributors.Count);
        }
    }

    private static List<(string NodeId, int Bytes)> TakeSliceSources(DownloadSession session, int blockIndex)
    {
        var result = new List<(string NodeId, int Bytes)>();
        foreach (var kv in session.SliceSources)
        {
            if (kv.Key.Block == blockIndex && session.SliceSources.TryRemove(kv.Key, out var src))
            {
                result.Add(src);
            }
        }
        return result;
    }

    private static int ExpectedSliceLength(PTorrentManifest manifest, int blockIndex, int sliceIndex)
    {
        int blockLength = manifest.GetBlockLength(blockIndex);
        long offset = (long)sliceIndex * manifest.SliceSizeBytes;
        if (offset >= blockLength) return 0;
        return (int)Math.Min(manifest.SliceSizeBytes, blockLength - offset);
    }

    private void ResetInFlightState(DownloadSession session)
    {
        session.Outstanding.Clear();
        foreach (var link in session.Links.Values)
        {
            Interlocked.Exchange(ref link.InFlight, 0);
        }
    }

    #endregion

    #region Peer links

    private void AttachChannelEvents(DownloadSession session, PeerLink link)
    {
        link.Channel.onclose += () =>
        {
            // Local close or transport teardown - release whatever was assigned to this peer.
            DropLink(session, link, "channel closed", excludeForSession: false);
        };

        link.Channel.onmessage += async (dc, protocol, data) =>
        {
            // async void handler: anything that escapes here would surface on the thread pool and
            // could take the process down, so the whole body is guarded.
            try
            {
                await HandleIncomingFrameAsync(session, link, data);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[DownloadCoordinator] Error handling frame from {Peer} in task {TaskId}.", link.NodeId, session.TaskId);
            }
        };
    }

    private async Task HandleIncomingFrameAsync(DownloadSession session, PeerLink link, byte[] data)
    {
        if (data == null || data.Length == 0 || link.Dropped) return;
        if (session.Cts.IsCancellationRequested) return; // paused/cancelled - late frames are ignored

        var parsed = _wireHandler.ParseFrame(data);
        if (!parsed.IsValid) return;

        switch (parsed.OpCode)
        {
            case TorrentOpCode.Bitfield:
            {
                var have = new SwarmBitfield(session.Manifest.TotalBlocksCount, parsed.Payload);
                link.RemoteHave = have;
                if (have.GetCompletedBlocksCount() == 0)
                {
                    // Seeder explicitly has nothing for us (not seeding this file / downloads
                    // switched off / stopped seeding). Since a SIPSorcery close() is local-only,
                    // this empty BITFIELD is the seeder's way of saying "go elsewhere".
                    DropLink(session, link, "advertises no blocks", excludeForSession: false);
                }
                break;
            }

            case TorrentOpCode.HaveBlock:
                link.RemoteHave?.SetBlock(parsed.BlockIndex, true);
                break;

            case TorrentOpCode.PieceSlice:
                await HandlePieceSliceAsync(session, link, parsed);
                break;
        }
    }

    private async Task HandlePieceSliceAsync(DownloadSession session, PeerLink link, ParsedWireFrame parsed)
    {
        var key = new SliceKey(parsed.BlockIndex, parsed.SliceIndex);

        // Only accept slices we actually asked THIS peer for. Unsolicited slices (or late answers to
        // a request that already expired and was re-assigned) are ignored - nobody can inject data
        // into a block they weren't asked to serve.
        if (!session.Outstanding.TryGetValue(key, out var request) ||
            !string.Equals(request.NodeId, link.NodeId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        if (!session.Outstanding.TryRemove(new KeyValuePair<SliceKey, SliceRequest>(key, request))) return;
        Interlocked.Decrement(ref link.InFlight);

        int expectedLength = ExpectedSliceLength(session.Manifest, parsed.BlockIndex, parsed.SliceIndex);
        if (parsed.Payload.Length != expectedLength)
        {
            // Wrong-sized slice can never verify. Not written; the slice is simply re-requested.
            _logger.LogDebug("[DownloadCoordinator] {Peer} sent slice {Block}/{Slice} of {Len} bytes (expected {Exp}) - ignored.",
                link.NodeId, parsed.BlockIndex, parsed.SliceIndex, parsed.Payload.Length, expectedLength);
            return;
        }

        bool written = await _storageEngine.WriteSliceAsync(session.TaskId, parsed.BlockIndex, parsed.SliceIndex, parsed.Payload);
        if (!written) return; // stays missing -> re-requested next loop

        session.SliceSources[key] = (link.NodeId, parsed.Payload.Length);
        session.DownloadMeter.Add(parsed.Payload.Length);
        _totalDownloadMeter.Add(parsed.Payload.Length);
        var slices = session.PendingSlices.GetOrAdd(parsed.BlockIndex, _ => new ConcurrentDictionary<int, bool>());
        slices[parsed.SliceIndex] = true;
    }

    /// <summary>
    /// Removes a peer from this session: frees its outstanding requests for immediate re-assignment,
    /// closes the channel locally, and keeps rediscovery from reconnecting it (for the rest of the
    /// session if excludeForSession, otherwise for PeerCooldown).
    /// </summary>
    private void DropLink(DownloadSession session, PeerLink link, string reason, bool excludeForSession)
    {
        if (link.Dropped) return;
        link.Dropped = true;

        session.Links.TryRemove(new KeyValuePair<string, PeerLink>(link.NodeId, link));

        foreach (var kv in session.Outstanding)
        {
            if (string.Equals(kv.Value.NodeId, link.NodeId, StringComparison.OrdinalIgnoreCase))
            {
                session.Outstanding.TryRemove(kv);
            }
        }
        Interlocked.Exchange(ref link.InFlight, 0);

        if (excludeForSession) session.Excluded[link.NodeId] = 1;
        else session.CooldownUntil[link.NodeId] = DateTime.UtcNow + PeerCooldown;

        try { if (link.Channel.readyState == RTCDataChannelState.open) link.Channel.close(); } catch { }

        UpdatePeerTelemetry(session);
        _logger.LogInformation("[DownloadCoordinator] Task {TaskId}: dropped peer {Peer} ({Reason}).", session.TaskId, link.NodeId, reason);
    }

    private void CloseAllLinks(DownloadSession session)
    {
        foreach (var link in session.Links.Values.ToList())
        {
            link.Dropped = true;
            try { if (link.Channel.readyState == RTCDataChannelState.open) link.Channel.close(); } catch { }
        }
        session.Links.Clear();
        session.Outstanding.Clear();
        UpdatePeerTelemetry(session);
    }

    private static void UpdatePeerTelemetry(DownloadSession session)
    {
        var open = session.Links.Values.Where(l => l.IsUsable).Select(l => l.NodeId).ToList();
        session.State.ActiveSeedersCount = open.Count;
        session.State.ConnectedPeerIds = open; // replaced, not mutated, so UI readers never see a list mid-change
    }

    private bool IsEligibleSeeder(DownloadSession session, TorrentPeerDescriptor seeder)
    {
        if (string.IsNullOrEmpty(seeder.NodeId)) return false;
        if (string.Equals(seeder.NodeId, _identity.NodeId, StringComparison.OrdinalIgnoreCase)) return false;
        if (session.Excluded.ContainsKey(seeder.NodeId)) return false;
        if (session.CooldownUntil.TryGetValue(seeder.NodeId, out var until) && until > DateTime.UtcNow) return false;
        if (_reputation.IsBanned(seeder.NodeId)) return false;
        return true;
    }

    /// <summary>
    /// Opens channels to the best eligible seeders (by file reputation) until this session has
    /// MaxPeerConnectionsPerFile usable links. Connection attempts run in parallel batches so one
    /// unreachable seeder doesn't serialize the rest. Returns how many new links were opened.
    /// </summary>
    private async Task<int> ConnectToSeedersAsync(DownloadSession session, IEnumerable<TorrentPeerDescriptor> seeders, CancellationToken token)
    {
        int maxLinks = Math.Max(1, _settings.CurrentValue.MaxPeerConnectionsPerFile);

        var queue = new Queue<TorrentPeerDescriptor>(seeders
            .Where(s => IsEligibleSeeder(session, s))
            .Where(s => !(session.Links.TryGetValue(s.NodeId, out var l) && l.IsAlive))
            .OrderByDescending(s => _reputation.GetScore(s.NodeId)));

        int newlyConnected = 0;
        while (queue.Count > 0 && !token.IsCancellationRequested)
        {
            int live = session.Links.Values.Count(l => l.IsAlive);
            int slots = maxLinks - live;
            if (slots <= 0) break;

            var batch = new List<TorrentPeerDescriptor>();
            while (batch.Count < slots && queue.Count > 0) batch.Add(queue.Dequeue());

            var results = await Task.WhenAll(batch.Select(s => TryOpenLinkAsync(session, s)));
            newlyConnected += results.Count(ok => ok);
        }

        UpdatePeerTelemetry(session);
        return newlyConnected;
    }

    private async Task<bool> TryOpenLinkAsync(DownloadSession session, TorrentPeerDescriptor seeder)
    {
        try
        {
            if (session.Links.TryGetValue(seeder.NodeId, out var stale) && !stale.IsAlive)
            {
                DropLink(session, stale, "stale channel replaced", excludeForSession: false);
                session.CooldownUntil.TryRemove(seeder.NodeId, out _); // we are reconnecting right now on purpose
            }

            var activeConn = _connectionManager.GetConnection(seeder.NodeId);
            if (activeConn == null || activeConn.PeerConnection?.connectionState != RTCPeerConnectionState.connected)
            {
                await _connectionManager.ConnectPeerAsync(seeder.NodeId, (seeder.SignalingEndpoints ?? new List<string>()).ToArray(), "outbound");
            }

            string channelLabel = $"unmanaged:file:{session.Manifest.ContentHash}";
            var channelResult = await _connectionManager.CreateUnmanagedDataChannelAsync(seeder.NodeId, channelLabel);
            if (channelResult.IsError || channelResult.Channel == null)
            {
                session.CooldownUntil[seeder.NodeId] = DateTime.UtcNow + PeerCooldown;
                return false;
            }

            var link = new PeerLink { NodeId = seeder.NodeId, Channel = channelResult.Channel };
            AttachChannelEvents(session, link);
            session.Links[seeder.NodeId] = link;
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[DownloadCoordinator] Task {TaskId}: could not open link to {Peer}.", session.TaskId, seeder.NodeId);
            session.CooldownUntil[seeder.NodeId] = DateTime.UtcNow + PeerCooldown;
            return false;
        }
    }

    /// <summary>
    /// Re-runs seeder discovery and connects to anything new. Called from the block loop when no
    /// currently-connected channel is usable - closes the gap where a download would otherwise
    /// stall forever against a stale, one-time seeder snapshot from task start.
    /// </summary>
    private async Task<int> RediscoverAndConnectSeedersAsync(DownloadSession session, CancellationToken token)
    {
        _logger.LogWarning("[DownloadCoordinator] Task {TaskId}: no usable connected seeders - rediscovering.", session.TaskId);
        var freshSeeders = await DiscoverSeedersAsync(session.Manifest);
        int newlyConnected = await ConnectToSeedersAsync(session, freshSeeders, token);
        _logger.LogInformation("[DownloadCoordinator] Task {TaskId}: rediscovery connected {Count} new seeder(s).", session.TaskId, newlyConnected);
        return newlyConnected;
    }

    #endregion

    #region Discovery

    private async Task<HashSet<TorrentPeerDescriptor>> DiscoverSeedersAsync(PTorrentManifest manifest)
    {
        // Discover seeders: manifest initial peers + local tracker registry + remote file_tracker
        // nodes + catalog consensus. TorrentPeerDescriptor has NodeId-based Equals/GetHashCode, so
        // the HashSet dedupes across all sources automatically.
        var seeders = new HashSet<TorrentPeerDescriptor>(manifest.InitialPeers.Where(p => !string.IsNullOrEmpty(p?.NodeId)));

        // Local registry - only populated if THIS node runs the file_tracker role.
        var trackerSeeders = await _trackerCoordinator.GetPeersForFileAsync(manifest.ContentHash);
        foreach (var s in trackerSeeders) seeders.Add(s);

        // Remote trackers - the client side of TrackerGetPeers.
        var remoteTrackerSeeders = await QueryRemoteTrackersForPeersAsync(manifest.ContentHash);
        foreach (var s in remoteTrackerSeeders) seeders.Add(s);

        // Anything the file-catalog consensus channel has learned via epidemic relay about who
        // hosts this content hash. !IsLocalSeeding filters out our own entry.
        var knownCatalogEntries = await _catalogChannel.GetAllKnownEntriesAsync();
        int catalogMatches = 0;
        foreach (var entry in knownCatalogEntries)
        {
            if (entry.IsLocalSeeding) continue;
            if (string.IsNullOrEmpty(entry.HostNodeId)) continue;
            if (!string.Equals(entry.Manifest?.ContentHash, manifest.ContentHash, StringComparison.OrdinalIgnoreCase)) continue;
            if (entry.HostNodeId.Equals(_identity.NodeId, StringComparison.OrdinalIgnoreCase)) continue;

            catalogMatches++;

            // A lightweight catalog record only carries endpoints if an admin allowed that extra
            // info type, so fall back to what the peer tracker already knows about this host.
            var endpoints = entry.HostSignalingEndpoints;
            if (endpoints == null || endpoints.Count == 0)
            {
                endpoints = _peerTracker.GetPeer(entry.HostNodeId)?.Identity.SignalingEndpoints ?? new List<string>();
            }

            seeders.Add(new TorrentPeerDescriptor
            {
                NodeId = entry.HostNodeId,
                SignalingEndpoints = endpoints
            });
        }

        // Never try to download from ourselves.
        seeders.RemoveWhere(p => string.Equals(p.NodeId, _identity.NodeId, StringComparison.OrdinalIgnoreCase));

        _logger.LogInformation("[DownloadCoordinator] Discovery for {Hash}: {Count} candidate seeders ({Manifest} manifest, {Tracker} local tracker, {Remote} remote tracker, {Catalog} catalog).",
            manifest.ContentHash, seeders.Count, manifest.InitialPeers.Count, trackerSeeders.Count, remoteTrackerSeeders.Count, catalogMatches);

        return seeders;
    }

    /// <summary>
    /// Asks every currently known "file_tracker" peer (in parallel) which nodes have announced
    /// themselves for this content hash. Failures/timeouts from individual trackers are logged and
    /// skipped - discovery always continues with whatever the other sources returned.
    /// </summary>
    private async Task<List<TorrentPeerDescriptor>> QueryRemoteTrackersForPeersAsync(string contentHash)
    {
        var trackerIds = _trackerAnnounce.GetKnownTrackerPeerIds();
        if (trackerIds.Count == 0) return new List<TorrentPeerDescriptor>();

        int timeoutSeconds = Math.Max(1, _settings.CurrentValue.SeederDiscoveryTimeoutSeconds);
        // The tracker handler reads the "contentHash" property (camelCase) from the request body.
        string requestJson = JsonSerializer.Serialize(new { contentHash });

        var queries = trackerIds.Select(async trackerId =>
        {
            try
            {
                var result = await _router.SendRequestAsync(
                    trackerId, "TrackerGetPeers", requestJson,
                    new RouteSettings { Timeout = TimeSpan.FromSeconds(timeoutSeconds) });

                if (!result.IsSuccess || string.IsNullOrEmpty(result.Payload))
                {
                    _logger.LogDebug("[DownloadCoordinator] TrackerGetPeers to {Tracker} for {Hash} failed: {Error}", trackerId, contentHash, result.Error);
                    return new List<TorrentPeerDescriptor>();
                }

                var peers = JsonSerializer.Deserialize<List<TorrentPeerDescriptor>>(result.Payload) ?? new List<TorrentPeerDescriptor>();
                foreach (var p in peers) p.SignalingEndpoints ??= new List<string>();
                return peers.Where(p => !string.IsNullOrEmpty(p?.NodeId)).ToList();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[DownloadCoordinator] Error querying tracker {Tracker} for {Hash}.", trackerId, contentHash);
                return new List<TorrentPeerDescriptor>();
            }
        });

        var all = await Task.WhenAll(queries);
        var merged = all.SelectMany(x => x).ToList();

        _logger.LogInformation("[DownloadCoordinator] Queried {TrackerCount} remote tracker(s) for {Hash}: {PeerCount} peer entr(ies).",
            trackerIds.Count, contentHash, merged.Count);
        return merged;
    }

    #endregion

    /// <summary>
    /// Registers a completed download with the archive so this node starts seeding it. Failure here
    /// never fails the download itself - the file is already on disk; it just won't be seeded.
    /// </summary>
    private async Task TrySeedCompletedDownloadAsync(DownloadSession session, string? finalPath)
    {
        if (!_settings.CurrentValue.SeedCompletedDownloads) return;
        if (string.IsNullOrEmpty(finalPath) || !File.Exists(finalPath))
        {
            _logger.LogWarning("[DownloadCoordinator] Task {TaskId}: completed but no output file to seed ({Path}).", session.TaskId, finalPath);
            return;
        }

        try
        {
            await _archiveService.RegisterDownloadedFileAsync(session.Manifest, finalPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[DownloadCoordinator] Task {TaskId}: download completed but registering it for seeding failed.", session.TaskId);
        }
    }

    public Task PauseDownloadAsync(string taskId)
    {
        if (_activeDownloads.TryGetValue(taskId, out var session))
        {
            // Running -> the pipeline stops and frees its slot; Queued -> simply leaves the queue.
            session.Cts.Cancel();
            session.State.Status = SwarmTaskStatus.Paused;
        }
        return Task.CompletedTask;
    }

    public Task ResumeDownloadAsync(string taskId)
    {
        if (_activeDownloads.TryGetValue(taskId, out var session) && session.State.Status == SwarmTaskStatus.Paused && !session.Running)
        {
            // Back into the queue (at the end) - it starts as soon as a download slot is free.
            session.Cts = new CancellationTokenSource();
            session.QueueOrder = Interlocked.Increment(ref _queueSequence);
            session.State.Status = SwarmTaskStatus.Queued;
            StartQueuedDownloads();
        }
        return Task.CompletedTask;
    }

    public async Task CancelDownloadAsync(string taskId)
    {
        if (_activeDownloads.TryRemove(taskId, out var session))
        {
            session.Cts.Cancel();
            session.State.Status = SwarmTaskStatus.Failed;
            CloseAllLinks(session);
            await _storageEngine.CleanupTaskStorageAsync(taskId);
        }
    }

    public SwarmTransferTaskState? GetTaskState(string taskId)
    {
        if (_activeDownloads.TryGetValue(taskId, out var session))
            return WithFreshRates(session);
        return null;
    }

    public List<SwarmTransferTaskState> GetAllTasks() => _activeDownloads.Values.Select(WithFreshRates).ToList();

    /// <summary>
    /// Speeds are computed only here, when the UI reads task state (page load / refresh) - nothing
    /// runs in the background for them. Download speed = accepted slices while the task runs;
    /// upload speed = what this node is serving of the same file (e.g. seeding it after completion).
    /// </summary>
    private SwarmTransferTaskState WithFreshRates(DownloadSession session)
    {
        var down = session.DownloadMeter.Snapshot();
        var up = _orchestrator.GetUploadStats(session.Manifest.ContentHash);

        session.State.DownloadSpeedBps = session.Running ? down.RateBps : 0;
        session.State.DownloadedLastMinuteBytes = down.LastMinuteBytes;
        session.State.TotalDownloadedBytes = down.TotalBytes;

        session.State.UploadSpeedBps = up.RateBps;
        session.State.UploadedLastMinuteBytes = up.LastMinuteBytes;
        session.State.TotalUploadedBytes = up.TotalBytes;
        return session.State;
    }

    public void Dispose()
    {
        foreach (var session in _activeDownloads.Values)
        {
            session.Cts.Cancel();
            CloseAllLinks(session);
        }
        _activeDownloads.Clear();
    }
}
