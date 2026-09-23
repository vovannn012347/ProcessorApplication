
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using FileSharingModule.Configuration;
using FileSharingModule.Models;
using FileSharingModule.Models.Torrent;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using P2PModule.Services.Connection;
using P2PModule.Services.Persistence;
using P2PModule.Services.Rating;

using SIPSorcery.Net;

namespace FileSharingModule.Services;

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
    private readonly IPeerTracker _peerTracker;
    private readonly IRatingService _ratingService;
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
        IPeerTracker peerTracker,
        IRatingService ratingService,
        IOptionsMonitor<FileSharingSettings> settings,
        ILogger<SwarmDownloadCoordinator> logger)
    {
        _connectionManager = connectionManager;
        _storageEngine = storageEngine;
        _wireHandler = wireHandler;
        _trackerCoordinator = trackerCoordinator;
        _peerTracker = peerTracker;
        _ratingService = ratingService;
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
            // 1. Discover seeders (manifest initial peers + tracker lookup)
            var seeders = new HashSet<TorrentPeerDescriptor>(session.Manifest.InitialPeers);
            var trackerSeeders = await _trackerCoordinator.GetPeersForFileAsync(session.Manifest.ContentHash);
            foreach (var s in trackerSeeders) seeders.Add(s);

            // 2. Connect to peers and establish unmanaged lanes
            foreach (var seeder in seeders)
            {
                if (token.IsCancellationRequested) break;

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
                }
            }

            session.State.ActiveSeedersCount = session.ActiveChannels.Count;

            // 3. Piece/Block scheduling loop
            for (int blockIndex = 0; blockIndex < session.Manifest.TotalBlocksCount; blockIndex++)
            {
                if (token.IsCancellationRequested) break;

                if (session.State.Bitfield.HasBlock(blockIndex)) continue;

                bool blockCompleted = await DownloadBlockAsync(session, blockIndex, token);
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
                        blockIndex--; // Retry block
                    }
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
