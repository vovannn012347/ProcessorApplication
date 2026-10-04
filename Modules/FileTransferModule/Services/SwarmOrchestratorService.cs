
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
}

public class SwarmOrchestratorService : ISwarmOrchestratorService, IDisposable
{
    private readonly IPeerConnectionManager _connectionManager;
    private readonly IFileChunkStorageEngine _storageEngine;
    private readonly ITorrentWireHandler _wireHandler;
    private readonly IFileTrackerCoordinator _trackerCoordinator;
    private readonly IOptionsMonitor<FileSharingSettings> _settings;
    private readonly ILogger<SwarmOrchestratorService> _logger;

    // ContentHash -> Active Seeding Record
    private readonly ConcurrentDictionary<string, ActiveSeedContext> _activeSeeds = new();

    private class ActiveSeedContext
    {
        public PTorrentManifest Manifest { get; set; } = new();
        public string SourceFilePath { get; set; } = string.Empty;
        public SwarmBitfield Bitfield { get; set; } = new(0);
    }

    public SwarmOrchestratorService(
        IPeerConnectionManager connectionManager,
        IFileChunkStorageEngine storageEngine,
        ITorrentWireHandler wireHandler,
        IFileTrackerCoordinator trackerCoordinator,
        IOptionsMonitor<FileSharingSettings> settings,
        ILogger<SwarmOrchestratorService> logger)
    {
        _connectionManager = connectionManager;
        _storageEngine = storageEngine;
        _wireHandler = wireHandler;
        _trackerCoordinator = trackerCoordinator;
        _settings = settings;
        _logger = logger;

        _connectionManager.OnUnmanagedChannelReceived += HandleIncomingUnmanagedChannel;
    }

    public async Task StartSeedingAsync(PTorrentManifest manifest, string localSourceFilePath)
    {
        if (manifest == null || string.IsNullOrEmpty(manifest.ContentHash) || !File.Exists(localSourceFilePath))
            return;

        var bitfield = new SwarmBitfield(manifest.TotalBlocksCount);
        for (int i = 0; i < manifest.TotalBlocksCount; i++) bitfield.SetBlock(i, true);

        var context = new ActiveSeedContext
        {
            Manifest = manifest,
            SourceFilePath = localSourceFilePath,
            Bitfield = bitfield
        };

        _activeSeeds[manifest.ContentHash] = context;
        _logger.LogInformation("[SwarmOrchestrator] Started seeding {FileName} (Hash: {Hash})", manifest.FileName, manifest.ContentHash);

        await _trackerCoordinator.RegisterManifestAsync(manifest);
    }

    public Task StopSeedingAsync(string contentHash)
    {
        if (_activeSeeds.TryRemove(contentHash, out var ctx))
        {
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

    private void HandleIncomingUnmanagedChannel(string senderNodeId, string channelLabel, RTCDataChannel channel)
    {
        // Expected format: "unmanaged:file:{contentHash}"
        if (!channelLabel.StartsWith("unmanaged:file:", StringComparison.OrdinalIgnoreCase)) return;

        string contentHash = channelLabel.Substring("unmanaged:file:".Length);
        if (!_activeSeeds.TryGetValue(contentHash, out var seedContext))
        {
            _logger.LogWarning("[SwarmOrchestrator] Received file channel for untracked hash {Hash} from {Sender}", contentHash, senderNodeId);
            return;
        }

        channel.onopen += () =>
        {
            _logger.LogDebug("[SwarmOrchestrator] Seeding channel opened with {Peer} for {Hash}", senderNodeId, contentHash);
            // 1. Send BITFIELD (0x01)
            byte[] bitfieldFrame = _wireHandler.BuildBitfieldFrame(seedContext.Bitfield);
            channel.send(bitfieldFrame);
        };

        channel.onmessage += async (dc, protocol, data) =>
        {
            if (data == null || data.Length == 0) return;

            var parsed = _wireHandler.ParseFrame(data);
            if (!parsed.IsValid) return;

            if (parsed.OpCode == TorrentOpCode.RequestSlice)
            {
                // Slice requested by leecher
                byte[]? sliceData = await _storageEngine.GetSliceDataAsync(
                    seedContext.Manifest,
                    parsed.BlockIndex,
                    parsed.SliceIndex,
                    seedContext.SourceFilePath);

                if (sliceData != null)
                {
                    byte[] pieceFrame = _wireHandler.BuildPieceSliceFrame(parsed.BlockIndex, parsed.SliceIndex, sliceData);
                    channel.send(pieceFrame);
                }
            }
        };
    }

    public void Dispose()
    {
        _connectionManager.OnUnmanagedChannelReceived -= HandleIncomingUnmanagedChannel;
        _activeSeeds.Clear();
    }
}