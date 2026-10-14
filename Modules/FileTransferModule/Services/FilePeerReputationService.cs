using System.Collections.Concurrent;
using System.Text.Json;

using FileTransferModule.Configuration;
using FileTransferModule.Models;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FileTransferModule.Services;

/// <summary>
/// Tracks how peers behave as file sources (see FilePeerReputation). Used by
/// SwarmDownloadCoordinator to rank seeders, skip banned ones, and record block outcomes.
/// Independent of P2PModule's IRatingService.
/// </summary>
public interface IFilePeerReputationService
{
    /// <summary>A block this peer contributed to passed verification.</summary>
    void RecordVerifiedContribution(string nodeId, long bytesContributed);

    /// <summary>
    /// A block this peer contributed to failed verification. soleSource = the peer supplied every
    /// slice of it (provable); otherwise it was one of several contributors (suspect only).
    /// </summary>
    void RecordBadBlock(string nodeId, bool soleSource);

    /// <summary>A slice request to this peer went unanswered past the timeout.</summary>
    void RecordSliceTimeout(string nodeId);

    bool IsBanned(string nodeId);

    /// <summary>Ranking score; 0 for unknown peers.</summary>
    double GetScore(string nodeId);

    FilePeerReputation? Get(string nodeId);

    List<FilePeerReputation> GetAll();

    /// <summary>Forgets everything about a peer (also lifts a ban). Returns false if unknown.</summary>
    bool Reset(string nodeId);
}

public class FilePeerReputationService : IFilePeerReputationService, IDisposable
{
    private readonly IOptionsMonitor<FileSharingSettings> _settings;
    private readonly ILogger<FilePeerReputationService> _logger;

    private readonly ConcurrentDictionary<string, FilePeerReputation> _records = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _writeLock = new();
    private readonly Timer _flushTimer;
    private volatile bool _dirty;

    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(15);

    public FilePeerReputationService(IOptionsMonitor<FileSharingSettings> settings, ILogger<FilePeerReputationService> logger)
    {
        _settings = settings;
        _logger = logger;

        Load();
        _flushTimer = new Timer(_ => FlushIfDirty(), null, FlushInterval, FlushInterval);
    }

    private string StorePath => Path.Combine(_settings.CurrentValue.StorageRootPath, "file_peer_reputation.json");

    private FilePeerReputation GetOrCreate(string nodeId) =>
        _records.GetOrAdd(nodeId, id => new FilePeerReputation { NodeId = id });

    public void RecordVerifiedContribution(string nodeId, long bytesContributed)
    {
        if (string.IsNullOrWhiteSpace(nodeId)) return;
        var r = GetOrCreate(nodeId);
        lock (r)
        {
            r.BlocksVerified++;
            r.BytesVerified += Math.Max(0, bytesContributed);
            r.LastUpdatedUtc = DateTime.UtcNow;
        }
        _dirty = true;
    }

    public void RecordBadBlock(string nodeId, bool soleSource)
    {
        if (string.IsNullOrWhiteSpace(nodeId)) return;
        var r = GetOrCreate(nodeId);
        bool bannedNow = false;

        lock (r)
        {
            var now = DateTime.UtcNow;
            r.LastUpdatedUtc = now;

            if (!soleSource)
            {
                r.BadBlocksSharedSource++;
            }
            else
            {
                r.BadBlocksSoleSource++;
                r.BadBlocksSinceLastBan++;

                int threshold = Math.Max(1, _settings.CurrentValue.PeerBanAfterBadBlocks);
                if (r.BadBlocksSinceLastBan >= threshold)
                {
                    r.BadBlocksSinceLastBan = 0;
                    r.BannedUntilUtc = now.AddHours(Math.Max(1, _settings.CurrentValue.PeerBanDurationHours));
                    bannedNow = true;
                }
            }
        }

        _dirty = true;

        if (bannedNow)
        {
            _logger.LogWarning("[FileReputation] Peer {NodeId} banned as a file source until {Until:u} after repeated corrupt blocks.", nodeId, r.BannedUntilUtc);
        }
        else
        {
            _logger.LogInformation("[FileReputation] Peer {NodeId} recorded a bad block ({Kind}).", nodeId, soleSource ? "sole source" : "shared source");
        }
    }

    public void RecordSliceTimeout(string nodeId)
    {
        if (string.IsNullOrWhiteSpace(nodeId)) return;
        var r = GetOrCreate(nodeId);
        lock (r)
        {
            r.SliceTimeouts++;
            r.LastUpdatedUtc = DateTime.UtcNow;
        }
        _dirty = true;
    }

    public bool IsBanned(string nodeId) =>
        !string.IsNullOrWhiteSpace(nodeId) && _records.TryGetValue(nodeId, out var r) && r.IsBanned(DateTime.UtcNow);

    public double GetScore(string nodeId) =>
        !string.IsNullOrWhiteSpace(nodeId) && _records.TryGetValue(nodeId, out var r) ? r.Score : 0.0;

    public FilePeerReputation? Get(string nodeId) =>
        !string.IsNullOrWhiteSpace(nodeId) && _records.TryGetValue(nodeId, out var r) ? r : null;

    public List<FilePeerReputation> GetAll() => _records.Values.OrderByDescending(r => r.Score).ToList();

    public bool Reset(string nodeId)
    {
        if (string.IsNullOrWhiteSpace(nodeId) || !_records.TryRemove(nodeId, out _)) return false;
        _dirty = true;
        _logger.LogInformation("[FileReputation] Reputation for {NodeId} reset.", nodeId);
        return true;
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(StorePath)) return;
            var items = JsonSerializer.Deserialize<List<FilePeerReputation>>(File.ReadAllText(StorePath));
            if (items == null) return;
            foreach (var r in items.Where(i => !string.IsNullOrWhiteSpace(i.NodeId)))
            {
                _records[r.NodeId] = r;
            }
            _logger.LogInformation("[FileReputation] Loaded {Count} peer reputation record(s).", _records.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[FileReputation] Could not read {Path}; starting empty.", StorePath);
        }
    }

    private void FlushIfDirty()
    {
        if (!_dirty) return;
        lock (_writeLock)
        {
            if (!_dirty) return;
            _dirty = false;
            try
            {
                Directory.CreateDirectory(_settings.CurrentValue.StorageRootPath);
                List<FilePeerReputation> snapshot;
                // Snapshot under each record's lock so we never serialize a half-updated record.
                snapshot = _records.Values.Select(r => { lock (r) { return Clone(r); } }).ToList();
                File.WriteAllText(StorePath, JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex)
            {
                _dirty = true; // retry next tick
                _logger.LogWarning(ex, "[FileReputation] Failed writing {Path}.", StorePath);
            }
        }
    }

    private static FilePeerReputation Clone(FilePeerReputation r) => new()
    {
        NodeId = r.NodeId,
        BlocksVerified = r.BlocksVerified,
        BytesVerified = r.BytesVerified,
        BadBlocksSoleSource = r.BadBlocksSoleSource,
        BadBlocksSharedSource = r.BadBlocksSharedSource,
        SliceTimeouts = r.SliceTimeouts,
        BadBlocksSinceLastBan = r.BadBlocksSinceLastBan,
        BannedUntilUtc = r.BannedUntilUtc,
        FirstSeenUtc = r.FirstSeenUtc,
        LastUpdatedUtc = r.LastUpdatedUtc
    };

    public void Dispose()
    {
        _flushTimer.Dispose();
        FlushIfDirty();
    }
}
