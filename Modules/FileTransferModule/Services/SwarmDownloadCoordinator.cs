
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using FileTransferModule.Configuration;
using FileTransferModule.Models;
using FileTransferModule.Models.Torrent;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using P2PModule.Interfaces;
using P2PModule.Services.Connection;
using P2PModule.Services.Persistence;
using P2PModule.Services.Rating;

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
}


public class SwarmDownloadCoordinator : ISwarmDownloadCoordinator, IDisposable
{
    private readonly IPeerConnectionManager _connectionManager;
    private readonly IFileChunkStorageEngine _storageEngine;
    private readonly ITorrentWireHandler _wireHandler;
    private readonly IFileTrackerCoordinator _trackerCoordinator;
    private readonly IFileCatalogConsensusChannel _catalogChannel;
    private readonly IPeerTracker _peerTracker;
    private readonly IRatingService _ratingService;
    private readonly INodeIdentityService _identity;
    private readonly IOptionsMonitor<FileSharingSettings> _settings;
    private readonly ILogger<SwarmDownloadCoordinator> _logger;

    private readonly ConcurrentDictionary<string, DownloadSession> _activeDownloads = new();

    private class DownloadSession
    {
        public string TaskId { get; set; } = string.Empty;
        public PTorrentManifest Manifest { get; set; } = new();
        public SwarmTransferTaskState State { get; set; } = new();
        public string? CustomTargetDirectory { get; set; }
        public string? CustomFileName { get; set; }
        public bool IsLocalAdmin { get; set; }
        public CancellationTokenSource Cts { get; set; } = new();

        // BlockIndex -> SliceIndex -> isReceived
        public ConcurrentDictionary<int, ConcurrentDictionary<int, bool>> PendingSlices { get; set; } = new();
        public ConcurrentDictionary<string, RTCDataChannel> ActiveChannels { get; set; } = new();
    }

    public SwarmDownloadCoordinator(
        IPeerConnectionManager connectionManager,
        IFileChunkStorageEngine storageEngine,
        ITorrentWireHandler wireHandler,
        IFileTrackerCoordinator trackerCoordinator,
        IFileCatalogConsensusChannel catalogChannel,
        IPeerTracker peerTracker,
        IRatingService ratingService,
        INodeIdentityService identity,
        IOptionsMonitor<FileSharingSettings> settings,
        ILogger<SwarmDownloadCoordinator> logger)
    {
        _connectionManager = connectionManager;
        _storageEngine = storageEngine;
        _wireHandler = wireHandler;
        _trackerCoordinator = trackerCoordinator;
        _catalogChannel = catalogChannel;
        _peerTracker = peerTracker;
        _ratingService = ratingService;
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
                Status = SwarmTaskStatus.Staging,
                StartedUtc = DateTime.UtcNow
            }
        };

        await _storageEngine.InitializeTaskStorageAsync(taskId, manifest);
        _activeDownloads[taskId] = session;

        _ = Task.Run(() => ExecuteDownloadPipelineAsync(session), session.Cts.Token);

        _logger.LogInformation("[DownloadCoordinator] Enqueued download task {TaskId} for file {Name}", taskId, manifest.FileName);
        return taskId;
    }

    private async Task ExecuteDownloadPipelineAsync(DownloadSession session)
    {
        var token = session.Cts.Token;
        try
        {
            // 1. Discover seeders and connect (manifest initial peers + tracker registry + catalog consensus)
            var seeders = await DiscoverSeedersAsync(session.Manifest);
            await ConnectToSeedersAsync(session, seeders, token);

            // 2. Piece/Block scheduling loop, with bounded retry + rediscovery on stall.
            // Previously a failed/timed-out block (DownloadBlockAsync returning false) was silently
            // skipped with no retry, so the bitfield could never complete and the task would just
            // hang with no error. Now each block gets up to MaxAttemptsPerBlock tries; if there is
            // no usable open channel, it rediscovers seeders (see RediscoverAndConnectSeedersAsync)
            // before retrying rather than giving up on the very first stall.
            const int maxAttemptsPerBlock = 10;

            for (int blockIndex = 0; blockIndex < session.Manifest.TotalBlocksCount; blockIndex++)
            {
                if (token.IsCancellationRequested) break;
                if (session.State.Bitfield.HasBlock(blockIndex)) continue;

                bool blockCompleted = false;
                int attempt = 0;

                while (!blockCompleted && attempt < maxAttemptsPerBlock && !token.IsCancellationRequested)
                {
                    attempt++;

                    bool hasOpenChannel = session.ActiveChannels.Values.Any(c => c.readyState == RTCDataChannelState.open);
                    if (!hasOpenChannel)
                    {
                        int found = await RediscoverAndConnectSeedersAsync(session, token);
                        if (found == 0)
                        {
                            // Genuinely nothing available right now - back off before trying again
                            // rather than hammering discovery every loop iteration.
                            await Task.Delay(TimeSpan.FromSeconds(10), token);
                            continue;
                        }
                    }

                    blockCompleted = await DownloadBlockAsync(session, blockIndex, token);

                    if (blockCompleted)
                    {
                        bool verified = await _storageEngine.VerifyAndCommitBlockAsync(session.TaskId, session.Manifest, blockIndex);
                        if (verified)
                        {
                            session.State.Bitfield.SetBlock(blockIndex, true);

                            // Broadcast HAVE_BLOCK (0x04)
                            byte[] haveFrame = _wireHandler.BuildHaveBlockFrame(blockIndex);
                            foreach (var ch in session.ActiveChannels.Values)
                            {
                                if (ch.readyState == RTCDataChannelState.open) ch.send(haveFrame);
                            }
                        }
                        else
                        {
                            // Penalize connected peers if block SHA-256 validation fails
                            _logger.LogWarning("[DownloadCoordinator] Block {BlockIndex} failed SHA-256 verification. Penalizing peers.", blockIndex);
                            foreach (var peerId in session.ActiveChannels.Keys)
                            {
                                await _ratingService.ApplyLocalReputationUpdateAsync(peerId, -5.0, isTrapdoor: false);
                            }
                            blockCompleted = false; // retry this block (still within the attempt loop/cap)
                        }
                    }
                    else
                    {
                        _logger.LogWarning("[DownloadCoordinator] Task {TaskId}: block {BlockIndex} attempt {Attempt}/{Max} failed or timed out.",
                            session.TaskId, blockIndex, attempt, maxAttemptsPerBlock);
                    }
                }

                if (!blockCompleted && !token.IsCancellationRequested)
                {
                    _logger.LogError("[DownloadCoordinator] Task {TaskId}: giving up on block {BlockIndex} after {Attempts} attempts - no seeders available.",
                        session.TaskId, blockIndex, attempt);
                    session.State.Status = SwarmTaskStatus.Failed;
                    session.State.ErrorMessage = $"Unable to download block {blockIndex} after {attempt} attempts - no seeders available.";
                    return;
                }
            }

            // 4. Final assembly and commit
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

                _logger.LogInformation("[DownloadCoordinator] Successfully completed task {TaskId}. Output: {Path}", session.TaskId, finalPath);
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

    private async Task<HashSet<TorrentPeerDescriptor>> DiscoverSeedersAsync(PTorrentManifest manifest)
    {
        // Discover seeders: manifest initial peers + tracker announce registry + catalog consensus.
        // TorrentPeerDescriptor has NodeId-based Equals/GetHashCode, so the HashSet dedupes
        // across all three sources automatically.
        var seeders = new HashSet<TorrentPeerDescriptor>(manifest.InitialPeers);

        var trackerSeeders = await _trackerCoordinator.GetPeersForFileAsync(manifest.ContentHash);
        foreach (var s in trackerSeeders) seeders.Add(s);

        // Anything the file-catalog consensus channel has learned via epidemic relay about who
        // hosts this content hash (see FileCatalogConsensusChannel / GenericChannelSyncService) -
        // a third, independent source alongside the manifest's baked-in peers and the
        // TrackerAnnounce registry. !IsLocalSeeding filters out our own entry.
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

        _logger.LogInformation("[DownloadCoordinator] Discovery for {Hash}: {Count} candidate seeders ({Manifest} manifest, {Tracker} tracker, {Catalog} catalog).",
            manifest.ContentHash, seeders.Count, manifest.InitialPeers.Count, trackerSeeders.Count, catalogMatches);

        return seeders;
    }

    /// <summary>
    /// Connects to any given seeders not already connected with an open channel for this session.
    /// Safe to call repeatedly (e.g. on rediscovery) - skips peers that already have a working
    /// channel rather than reconnecting them. Returns how many new channels were opened.
    /// </summary>
    private async Task<int> ConnectToSeedersAsync(DownloadSession session, IEnumerable<TorrentPeerDescriptor> seeders, CancellationToken token)
    {
        int newlyConnected = 0;

        foreach (var seeder in seeders)
        {
            if (token.IsCancellationRequested) break;

            if (session.ActiveChannels.TryGetValue(seeder.NodeId, out var existingChannel) &&
                existingChannel.readyState == RTCDataChannelState.open)
            {
                continue; // already have a working channel to this seeder
            }

            var activeConn = _connectionManager.GetConnection(seeder.NodeId);
            if (activeConn == null || activeConn.PeerConnection?.connectionState != RTCPeerConnectionState.connected)
            {
                await _connectionManager.ConnectPeerAsync(seeder.NodeId, seeder.SignalingEndpoints.ToArray(), "outbound");
            }

            string channelLabel = $"unmanaged:file:{session.Manifest.ContentHash}";
            var channelResult = await _connectionManager.CreateUnmanagedDataChannelAsync(seeder.NodeId, channelLabel);
            if (!channelResult.IsError && channelResult.Channel != null)
            {
                AttachChannelEvents(session, channelResult.Channel);
                session.ActiveChannels[seeder.NodeId] = channelResult.Channel;
                session.State.ConnectedPeerIds.Add(seeder.NodeId);
                newlyConnected++;
            }
        }

        session.State.ActiveSeedersCount = session.ActiveChannels.Values.Count(c => c.readyState == RTCDataChannelState.open);
        return newlyConnected;
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

    private async Task<bool> DownloadBlockAsync(DownloadSession session, int blockIndex, CancellationToken token)
    {
        int sliceCount = session.Manifest.GetSlicesCountForBlock(blockIndex);
        var sliceTracker = session.PendingSlices.GetOrAdd(blockIndex, _ => new ConcurrentDictionary<int, bool>());

        var channelList = session.ActiveChannels.Values.Where(c => c.readyState == RTCDataChannelState.open).ToList();
        if (channelList.Count == 0)
        {
            await Task.Delay(500, token);
            return false;
        }

        for (int sliceIndex = 0; sliceIndex < sliceCount; sliceIndex++)
        {
            if (token.IsCancellationRequested) return false;

            if (sliceTracker.TryGetValue(sliceIndex, out bool done) && done) continue;

            // Round-robin slice request across active channels
            var targetChannel = channelList[sliceIndex % channelList.Count];
            byte[] requestFrame = _wireHandler.BuildRequestSliceFrame(blockIndex, sliceIndex);
            targetChannel.send(requestFrame);
        }

        // Wait for all slices of the current block to arrive
        int timeoutSeconds = _settings.CurrentValue.SliceRequestTimeoutSeconds;
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);

        while (DateTime.UtcNow < deadline)
        {
            if (token.IsCancellationRequested) return false;

            if (sliceTracker.Count == sliceCount && sliceTracker.Values.All(v => v))
            {
                return true;
            }
            await Task.Delay(50, token);
        }

        return false;
    }

    private void AttachChannelEvents(DownloadSession session, RTCDataChannel channel)
    {
        channel.onmessage += async (dc, protocol, data) =>
        {
            if (data == null || data.Length == 0) return;

            var parsed = _wireHandler.ParseFrame(data);
            if (!parsed.IsValid) return;

            if (parsed.OpCode == TorrentOpCode.PieceSlice)
            {
                bool written = await _storageEngine.WriteSliceAsync(session.TaskId, parsed.BlockIndex, parsed.SliceIndex, parsed.Payload);
                if (written)
                {
                    var slices = session.PendingSlices.GetOrAdd(parsed.BlockIndex, _ => new ConcurrentDictionary<int, bool>());
                    slices[parsed.SliceIndex] = true;
                }
            }
        };
    }

    public Task PauseDownloadAsync(string taskId)
    {
        if (_activeDownloads.TryGetValue(taskId, out var session))
        {
            session.Cts.Cancel();
            session.State.Status = SwarmTaskStatus.Paused;
        }
        return Task.CompletedTask;
    }

    public Task ResumeDownloadAsync(string taskId)
    {
        if (_activeDownloads.TryGetValue(taskId, out var session) && session.State.Status == SwarmTaskStatus.Paused)
        {
            session.Cts = new CancellationTokenSource();
            session.State.Status = SwarmTaskStatus.Staging;
            _ = Task.Run(() => ExecuteDownloadPipelineAsync(session), session.Cts.Token);
        }
        return Task.CompletedTask;
    }

    public async Task CancelDownloadAsync(string taskId)
    {
        if (_activeDownloads.TryRemove(taskId, out var session))
        {
            session.Cts.Cancel();
            session.State.Status = SwarmTaskStatus.Failed;
            await _storageEngine.CleanupTaskStorageAsync(taskId);
        }
    }

    public SwarmTransferTaskState? GetTaskState(string taskId)
    {
        if (_activeDownloads.TryGetValue(taskId, out var session))
            return session.State;
        return null;
    }

    public List<SwarmTransferTaskState> GetAllTasks() => _activeDownloads.Values.Select(s => s.State).ToList();

    public void Dispose()
    {
        foreach (var session in _activeDownloads.Values)
        {
            session.Cts.Cancel();
        }
        _activeDownloads.Clear();
    }
}
