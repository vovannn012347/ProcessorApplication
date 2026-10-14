
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Channels;
using System.Threading.Tasks;

using FileTransferModule.Configuration;
using FileTransferModule.Models;
using FileTransferModule.Models.Torrent;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using P2PModule.Services.Connection;

using SIPSorcery.Net;

namespace FileTransferModule.Services;
public interface ISwarmOrchestratorService
{
    /// <summary>
    /// Registers a local completed file for active seeding in the swarm.
    /// </summary>
    Task StartSeedingAsync(PTorrentManifest manifest, string localSourceFilePath);

    /// <summary>
    /// Stops seeding a file.
    /// </summary>
    Task StopSeedingAsync(string contentHash);

    /// <summary>
    /// Retrieves the local bitfield (typically 100% complete for seeders).
    /// </summary>
    SwarmBitfield? GetLocalBitfield(string contentHash);

    /// <summary>
    /// Checks whether a file is currently being seeded.
    /// </summary>
    bool IsSeeding(string contentHash);

    /// <summary>Current upload rate for one file (bytes/s, averaged over the last few seconds). 0 if idle.</summary>
    double GetUploadRateBps(string contentHash);

    /// <summary>Current node-wide upload rate (bytes/s).</summary>
    double GetTotalUploadRateBps();

    /// <summary>Upload channels currently being served (slots in use).</summary>
    int ActiveUploadSlots { get; }

    /// <summary>Upload rate / last minute / total for one file.</summary>
    TransferDirectionStats GetUploadStats(string contentHash);

    /// <summary>Node-wide upload rate / last minute / total.</summary>
    TransferDirectionStats GetTotalUploadStats();

    /// <summary>Files with open upload channels or upload traffic in the last minute.</summary>
    List<UploadFileStats> GetActiveUploads();
}

public class SwarmOrchestratorService : ISwarmOrchestratorService, IDisposable
{
    private readonly IPeerConnectionManager _connectionManager;
    private readonly IFileChunkStorageEngine _storageEngine;
    private readonly ITorrentWireHandler _wireHandler;
    private readonly IOptionsMonitor<FileSharingSettings> _settings;
    private readonly ILogger<SwarmOrchestratorService> _logger;

    // ContentHash -> Active Seeding Record
    private readonly ConcurrentDictionary<string, ActiveSeedContext> _activeSeeds = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Longest a single slice waits for the send buffer to drain before it is dropped (the downloader re-requests it).</summary>
    private static readonly TimeSpan MaxSendBufferWait = TimeSpan.FromSeconds(30);

    // Upload accounting: per-file and node-wide rate meters (read on demand by the UI), a node-wide
    // bandwidth limiter, and a lock that makes the slot check + slot take atomic.
    private readonly ConcurrentDictionary<string, TransferRateMeter> _uploadMeters = new(StringComparer.OrdinalIgnoreCase);
    private readonly TransferRateMeter _totalUploadMeter = new();
    private readonly UploadRateLimiter _uploadLimiter = new();
    private readonly object _slotLock = new();

    private class ActiveSeedContext
    {
        public PTorrentManifest Manifest { get; set; } = new();
        public string SourceFilePath { get; set; } = string.Empty;
        public SwarmBitfield Bitfield { get; set; } = new(0);

        /// <summary>Every channel currently being served for this file, so StopSeeding can end them.</summary>
        public ConcurrentDictionary<ServeChannel, byte> Channels { get; } = new();
    }

    /// <summary>
    /// One downloader's channel. Requests go into a bounded queue and are served one at a time by
    /// a single loop, which waits for the SCTP send buffer to drain below the high-water mark
    /// before each send. Previously every REQUEST_SLICE ran its own concurrent read+send with no
    /// limit, so a fast downloader could pile up an unbounded send buffer and drop the channel.
    /// </summary>
    private sealed class ServeChannel
    {
        public string PeerNodeId { get; init; } = string.Empty;
        public RTCDataChannel Channel { get; init; } = null!;
        public System.Threading.Channels.Channel<(int Block, int Slice)> Queue { get; init; } = null!;
        public CancellationTokenSource Cts { get; } = new();
    }

    public SwarmOrchestratorService(
        IPeerConnectionManager connectionManager,
        IFileChunkStorageEngine storageEngine,
        ITorrentWireHandler wireHandler,
        IOptionsMonitor<FileSharingSettings> settings,
        ILogger<SwarmOrchestratorService> logger)
    {
        _connectionManager = connectionManager;
        _storageEngine = storageEngine;
        _wireHandler = wireHandler;
        _settings = settings;
        _logger = logger;

        _connectionManager.OnUnmanagedChannelReceived += HandleIncomingUnmanagedChannel;
    }

    public Task StartSeedingAsync(PTorrentManifest manifest, string localSourceFilePath)
    {
        if (manifest == null || string.IsNullOrEmpty(manifest.ContentHash) || !File.Exists(localSourceFilePath))
            return Task.CompletedTask;

        var bitfield = new SwarmBitfield(manifest.TotalBlocksCount);
        for (int i = 0; i < manifest.TotalBlocksCount; i++) bitfield.SetBlock(i, true);

        var context = new ActiveSeedContext
        {
            Manifest = manifest,
            SourceFilePath = localSourceFilePath,
            Bitfield = bitfield
        };

        // Re-seeding the same hash (e.g. AllowDownload toggled back on) replaces the old context;
        // end any channels still attached to the old one so they don't keep serving stale state.
        if (_activeSeeds.TryRemove(manifest.ContentHash, out var previous))
        {
            EndAllChannels(previous, "re-seeded");
        }

        _activeSeeds[manifest.ContentHash] = context;
        _logger.LogInformation("[SwarmOrchestrator] Started seeding {FileName} (Hash: {Hash})", manifest.FileName, manifest.ContentHash);

        // NOTE: intentionally does NOT notify IFileTrackerCoordinator here. Seeding is only ever
        // started by FileArchiveService for files it tracks, and FileTrackerCoordinator resolves
        // local files through IFileArchiveService directly. Depending on the tracker from here
        // created a DI cycle: FileTrackerCoordinator -> FileArchiveService -> SwarmOrchestratorService -> FileTrackerCoordinator.
        return Task.CompletedTask;
    }

    public Task StopSeedingAsync(string contentHash)
    {
        if (_activeSeeds.TryRemove(contentHash, out var ctx))
        {
            // Previously this only removed the dictionary entry: channels already open kept their
            // captured context and went on serving slices (or failing reads, if the file was deleted).
            EndAllChannels(ctx, "seeding stopped");
            _logger.LogInformation("[SwarmOrchestrator] Stopped seeding {Hash}", contentHash);
        }
        return Task.CompletedTask;
    }

    public SwarmBitfield? GetLocalBitfield(string contentHash)
    {
        if (_activeSeeds.TryGetValue(contentHash, out var ctx))
            return ctx.Bitfield;
        return null;
    }

    public bool IsSeeding(string contentHash) => _activeSeeds.ContainsKey(contentHash);

    public double GetUploadRateBps(string contentHash) =>
        !string.IsNullOrEmpty(contentHash) && _uploadMeters.TryGetValue(contentHash, out var meter) ? meter.BytesPerSecond() : 0;

    public double GetTotalUploadRateBps() => _totalUploadMeter.BytesPerSecond();

    public int ActiveUploadSlots => _activeSeeds.Values.Sum(c => c.Channels.Count);

    public TransferDirectionStats GetUploadStats(string contentHash) =>
        !string.IsNullOrEmpty(contentHash) && _uploadMeters.TryGetValue(contentHash, out var meter) ? meter.Snapshot() : TransferDirectionStats.Empty;

    public TransferDirectionStats GetTotalUploadStats() => _totalUploadMeter.Snapshot();

    public List<UploadFileStats> GetActiveUploads()
    {
        var result = new List<UploadFileStats>();
        foreach (var (hash, meter) in _uploadMeters)
        {
            _activeSeeds.TryGetValue(hash, out var ctx);
            int channels = ctx?.Channels.Count ?? 0;
            var stats = meter.Snapshot();
            if (channels == 0 && stats.LastMinuteBytes == 0) continue; // idle - not shown

            result.Add(new UploadFileStats
            {
                ContentHash = hash,
                FileName = ctx?.Manifest.FileName ?? hash,
                ActiveChannels = channels,
                Upload = stats
            });
        }
        return result.OrderByDescending(u => u.Upload.RateBps).ThenBy(u => u.FileName).ToList();
    }

    /// <summary>
    /// Extracts the content hash from a file channel label. PeerConnectionManager strips the
    /// "unmanaged:" prefix before raising OnUnmanagedChannelReceived, so the label arrives as
    /// "file:{hash}". The old check required "unmanaged:file:" and therefore never matched -
    /// no seeder ever served a single slice. Both forms are accepted.
    /// </summary>
    private static string? TryGetContentHashFromLabel(string channelLabel)
    {
        if (string.IsNullOrEmpty(channelLabel)) return null;

        const string managedPrefix = "unmanaged:file:";
        const string cleanPrefix = "file:";

        if (channelLabel.StartsWith(managedPrefix, StringComparison.OrdinalIgnoreCase))
            return channelLabel.Substring(managedPrefix.Length);
        if (channelLabel.StartsWith(cleanPrefix, StringComparison.OrdinalIgnoreCase))
            return channelLabel.Substring(cleanPrefix.Length);
        return null;
    }

    private void HandleIncomingUnmanagedChannel(string senderNodeId, string channelLabel, RTCDataChannel channel)
    {
        string? contentHash = TryGetContentHashFromLabel(channelLabel);
        if (string.IsNullOrEmpty(contentHash)) return;

        if (!_activeSeeds.TryGetValue(contentHash, out var seedContext))
        {
            // Not seeding this hash (never had it, or AllowDownload switched off). Tell the leecher
            // with an empty BITFIELD ("I have nothing") - SIPSorcery's close() is local-only, so
            // closing alone would leave the leecher waiting out its slice timeouts.
            _logger.LogWarning("[SwarmOrchestrator] Received file channel for untracked hash {Hash} from {Sender} - refusing.", contentHash, senderNodeId);
            RefuseChannel(channel);
            return;
        }

        int queueLimit = Math.Max(1, _settings.CurrentValue.UploadQueueLimitPerChannel);
        var serve = new ServeChannel
        {
            PeerNodeId = senderNodeId,
            Channel = channel,
            // DropWrite: when the queue is full, new requests are discarded; the downloader's own
            // timeout re-requests them. Memory per channel stays bounded no matter what it sends.
            Queue = System.Threading.Channels.Channel.CreateBounded<(int Block, int Slice)>(new BoundedChannelOptions(queueLimit)
            {
                FullMode = BoundedChannelFullMode.DropWrite,
                SingleReader = true,
                SingleWriter = false
            })
        };
        // Upload slots: node-wide and per peer. Checked and taken atomically so concurrent arrivals
        // can't overshoot. Over the limit -> "nothing here" (empty BITFIELD); the downloader drops
        // us, tries other seeders, and retries this node after its cooldown.
        lock (_slotLock)
        {
            int maxSlots = Math.Max(1, _settings.CurrentValue.MaxUploadSlots);
            int maxPerPeer = Math.Max(1, _settings.CurrentValue.MaxUploadSlotsPerPeer);
            int inUse = 0, peerInUse = 0;
            foreach (var ctx in _activeSeeds.Values)
            {
                foreach (var s in ctx.Channels.Keys)
                {
                    inUse++;
                    if (string.Equals(s.PeerNodeId, senderNodeId, StringComparison.OrdinalIgnoreCase)) peerInUse++;
                }
            }

            if (inUse >= maxSlots || peerInUse >= maxPerPeer)
            {
                _logger.LogInformation("[SwarmOrchestrator] Upload slots full ({InUse}/{Max} total, {PeerInUse}/{MaxPerPeer} for {Peer}) - refusing channel for {Hash}.",
                    inUse, maxSlots, peerInUse, maxPerPeer, senderNodeId, contentHash);
                RefuseChannel(channel);
                return;
            }

            seedContext.Channels[serve] = 1;
        }

        // StopSeeding may have run between the lookup above and registering this channel; if so,
        // the context we attached to is already being torn down - refuse instead of serving it.
        if (!_activeSeeds.TryGetValue(contentHash, out var current) || !ReferenceEquals(current, seedContext))
        {
            seedContext.Channels.TryRemove(serve, out _);
            RefuseChannel(channel);
            return;
        }

        void SendBitfield()
        {
            try { channel.send(_wireHandler.BuildBitfieldFrame(seedContext.Bitfield)); }
            catch (Exception ex) { _logger.LogDebug(ex, "[SwarmOrchestrator] Could not send BITFIELD to {Peer}.", senderNodeId); }
        }

        if (channel.readyState == RTCDataChannelState.open)
        {
            SendBitfield();
        }
        else
        {
            channel.onopen += () =>
            {
                _logger.LogDebug("[SwarmOrchestrator] Seeding channel opened with {Peer} for {Hash}", senderNodeId, contentHash);
                SendBitfield();
            };
        }

        channel.onmessage += (dc, protocol, data) =>
        {
            if (data == null || data.Length == 0 || serve.Cts.IsCancellationRequested) return;

            var parsed = _wireHandler.ParseFrame(data);
            if (!parsed.IsValid || parsed.OpCode != TorrentOpCode.RequestSlice) return;

            if (!serve.Queue.Writer.TryWrite((parsed.BlockIndex, parsed.SliceIndex)))
            {
                _logger.LogDebug("[SwarmOrchestrator] Upload queue full for {Peer} on {Hash}; dropped request {Block}/{Slice}.",
                    senderNodeId, contentHash, parsed.BlockIndex, parsed.SliceIndex);
            }
        };

        channel.onclose += () => EndChannel(seedContext, serve, sendEmptyBitfield: false);

        _ = Task.Run(() => ServeLoopAsync(seedContext, serve));
    }

    private async Task ServeLoopAsync(ActiveSeedContext ctx, ServeChannel serve)
    {
        var token = serve.Cts.Token;
        try
        {
            await foreach (var (blockIndex, sliceIndex) in serve.Queue.Reader.ReadAllAsync(token))
            {
                if (token.IsCancellationRequested) break;
                if (blockIndex < 0 || blockIndex >= ctx.Manifest.TotalBlocksCount) continue;
                if (sliceIndex < 0 || sliceIndex >= ctx.Manifest.GetSlicesCountForBlock(blockIndex)) continue;

                byte[]? sliceData = await _storageEngine.GetSliceDataAsync(ctx.Manifest, blockIndex, sliceIndex, ctx.SourceFilePath);
                if (sliceData == null) continue;

                if (!await WaitForSendBufferAsync(serve, token))
                {
                    if (serve.Channel.readyState != RTCDataChannelState.open) break;
                    continue; // buffer never drained in time - drop this one, downloader re-requests
                }

                var frame = _wireHandler.BuildPieceSliceFrame(blockIndex, sliceIndex, sliceData);

                // Node-wide bandwidth cap (0 = unlimited; read each time so changes apply live).
                await _uploadLimiter.WaitAsync(frame.Length, (int)Math.Min((long)Math.Max(0, _settings.CurrentValue.MaxUploadRateKBps) * 1024, int.MaxValue), token);

                serve.Channel.send(frame);

                _uploadMeters.GetOrAdd(ctx.Manifest.ContentHash, _ => new TransferRateMeter()).Add(sliceData.Length);
                _totalUploadMeter.Add(sliceData.Length);
            }
        }
        catch (OperationCanceledException)
        {
            // channel ended
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[SwarmOrchestrator] Serve loop for {Peer} on {Hash} failed.", serve.PeerNodeId, ctx.Manifest.ContentHash);
        }
        finally
        {
            ctx.Channels.TryRemove(serve, out _);
        }
    }

    /// <summary>
    /// Waits until the channel's SCTP send buffer is below UploadSendBufferHighWaterBytes.
    /// SIPSorcery 6.x exposes bufferedAmount but no bufferedamountlow event, so this polls.
    /// Returns false if the channel closed or the buffer didn't drain within MaxSendBufferWait.
    /// </summary>
    private async Task<bool> WaitForSendBufferAsync(ServeChannel serve, CancellationToken token)
    {
        ulong highWater = (ulong)Math.Max(65536, _settings.CurrentValue.UploadSendBufferHighWaterBytes);
        var deadline = DateTime.UtcNow + MaxSendBufferWait;

        while (true)
        {
            if (serve.Channel.readyState != RTCDataChannelState.open) return false;
            if (serve.Channel.bufferedAmount < highWater) return true;
            if (DateTime.UtcNow > deadline) return false;
            await Task.Delay(5, token);
        }
    }

    private void EndAllChannels(ActiveSeedContext ctx, string reason)
    {
        var channels = ctx.Channels.Keys.ToList();
        foreach (var serve in channels)
        {
            EndChannel(ctx, serve, sendEmptyBitfield: true);
        }
        if (channels.Count > 0)
        {
            _logger.LogInformation("[SwarmOrchestrator] Ended {Count} active upload channel(s) for {Hash} ({Reason}).", channels.Count, ctx.Manifest.ContentHash, reason);
        }
    }

    /// <summary>
    /// Stops serving one channel. With sendEmptyBitfield the leecher is told "I have nothing" first,
    /// which is what actually makes it move on (a SIPSorcery close() is not seen by the remote side).
    /// </summary>
    private void EndChannel(ActiveSeedContext ctx, ServeChannel serve, bool sendEmptyBitfield)
    {
        if (!ctx.Channels.TryRemove(serve, out _) && serve.Cts.IsCancellationRequested) return;

        try { serve.Cts.Cancel(); } catch { }
        serve.Queue.Writer.TryComplete();

        try
        {
            if (serve.Channel.readyState == RTCDataChannelState.open)
            {
                if (sendEmptyBitfield) serve.Channel.send(_wireHandler.BuildBitfieldFrame(new SwarmBitfield(0)));
                serve.Channel.close();
            }
        }
        catch
        {
            // best effort
        }
    }

    private void RefuseChannel(RTCDataChannel channel)
    {
        void Refuse()
        {
            try
            {
                channel.send(_wireHandler.BuildBitfieldFrame(new SwarmBitfield(0)));
                channel.close();
            }
            catch
            {
                // best effort
            }
        }

        try
        {
            if (channel.readyState == RTCDataChannelState.open) Refuse();
            else channel.onopen += Refuse;
        }
        catch
        {
            // best effort
        }
    }

    public void Dispose()
    {
        _connectionManager.OnUnmanagedChannelReceived -= HandleIncomingUnmanagedChannel;
        foreach (var ctx in _activeSeeds.Values)
        {
            EndAllChannels(ctx, "shutdown");
        }
        _activeSeeds.Clear();
    }
}