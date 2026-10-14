// [full-implementation]
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using P2PModule.Configuration;
using P2PModule.Interfaces;
using P2PModule.Models;
using ProcessorApplication.Utils;

namespace P2PModule.Services.Persistence;


public class HistoricalNodeInfo
{
    public string NodeId { get; set; } = string.Empty;
    public string PublicEncryptionKey { get; set; } = string.Empty;
    public string PublicVerifyKey { get; set; } = string.Empty;
    public DateTime RecordedDate { get; set; }
}

/// <summary>
/// Handles historical file-oriented consensus shards, immutable indices in /history, 
/// daily merges in /merged_today_history, and cryptographic manifest tracking.
/// </summary>
public class ConsensusStore : IConsensusStore, IDisposable
{
    private readonly ILogger<ConsensusStore> _logger;
    private readonly IOptionsMonitor<P2PSettings> _p2pSettings;
    private readonly IOptionsMonitor<StorageSettings> _storageSettings;

    private readonly IDisposable _settingsChangeListener;
    private readonly IDisposable _settingsChangeListener2;

    private string _latestConsensusDirName = string.Empty;
    private string _historyDir = string.Empty;
    private string _mergeBaseDir = string.Empty;

    public ConsensusStore(
        ILogger<ConsensusStore> logger,
        IOptionsMonitor<P2PSettings> p2pSettings,
        IOptionsMonitor<StorageSettings> storageSettings)
    {
        _p2pSettings = p2pSettings;
        _logger = logger;
        _storageSettings = storageSettings;


        UpdateSettings();
        InitializeAsync();

        _settingsChangeListener = _p2pSettings.OnChangeDebounced(_ => UpdateSettings());
        _settingsChangeListener2 = _storageSettings.OnChangeDebounced(_ => UpdateSettings());
    }

    private void UpdateSettings()
    {
        string basePath = _p2pSettings.CurrentValue.DataStoragePath ?? "./Module_Data/P2P";
        _historyDir = Path.Combine(basePath, "history");
        _mergeBaseDir = Path.Combine(basePath, "merged_today_history");
    }

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_historyDir);
        Directory.CreateDirectory(_mergeBaseDir);

        var days = GetAvailableHistoricalDays();
        _latestConsensusDirName = days.FirstOrDefault() ?? DateTime.UtcNow.ToString("yyyy-MM-dd");

        return Task.CompletedTask;
    }

    public List<string> GetAvailableHistoricalDays()
    {
        if (!Directory.Exists(_historyDir)) 
            return new List<string>();

        return Directory.GetDirectories(_historyDir)
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrEmpty(name) && name.Length == 10)
            .OrderByDescending(n => n)
            .ToList()!;
    }

    public PeerNodeHistoric? GetHistoricalPeer(string dayKey, string nodeId)
    {
        string filePath = Path.Combine(_historyDir, dayKey, "peers", $"{nodeId}.json");
        if (!File.Exists(filePath)) return null;

        try
        {
            string json = File.ReadAllText(filePath);
            return JsonSerializer.Deserialize<PeerNodeHistoric>(json);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Consensus Store] Failed reading historical node {Id} for date {Day}", nodeId, dayKey);
            return null;
        }
    }

    public List<string> GetHistoricalPeers(string dayKey)
    {
        string peerDir = Path.Combine(_historyDir, dayKey, "peers");
        if (!Directory.Exists(peerDir)) return new List<string>();

        string[] files = Directory.GetFiles(peerDir, "*.json");
        if (files.Length == 0) return new List<string>();

        return files.Select(f => Path.GetFileNameWithoutExtension(f)).ToList();
    }


    public bool IsOutdated()
    {
        string todayDirName = DateTime.UtcNow.ToString("yyyy-MM-dd");

        // If today's directory hasn't been written or we have skipped a day, we are outdated
        string todayPath = Path.Combine(_historyDir, todayDirName);
        return _latestConsensusDirName != todayDirName || !Directory.Exists(todayPath) || !File.Exists(Path.Combine(todayPath, "manifest.txt"));
    }

    /// <summary>
    /// Implemented: Writes updated configuration payloads directly into daily history directory paths.
    /// to delete - pass null in peer
    /// </summary>
    public void UpdateHistoricalPeer(string dayKey, string nodeId, PeerNodeHistoric peer)
    {
        if (string.IsNullOrEmpty(dayKey) || string.IsNullOrEmpty(nodeId)) return;

        try
        {
            if(peer != null)
            {
                string targetDir = Path.Combine(_historyDir, dayKey, "peers");
                Directory.CreateDirectory(targetDir);

                string targetFile = Path.Combine(targetDir, $"{nodeId}.json");
                string payload = JsonSerializer.Serialize(peer, new JsonSerializerOptions { WriteIndented = true });

                File.WriteAllText(targetFile, payload);
                _logger.LogDebug("[Consensus Store] Historical peer {NodeId} successfully persisted for epoch {Day}", nodeId, dayKey);
            }
            else
            {
                string targetDir = Path.Combine(_historyDir, dayKey, "peers");
                string targetFile = Path.Combine(targetDir, $"{nodeId}.json");

                if (File.Exists(targetFile))
                    File.Delete(targetFile);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Consensus Store] Failed to update historical peer record on disk branch partition.");
        }
    }

    /// <summary>
    /// Gathers historical epochs and deletes peer records exceeding the RetainedHistoryEpochsCount limits.
    /// Node-Aware Retention: Safely retains at least RetainedNodeHistoryEpochs records per peer, even if they 
    /// have gone silent, protecting future handshake connections against key-loss errors.
    /// </summary>
    public Task PruneHistoryAsync()
    {
        try
        {
            int daysToKeep = _storageSettings.CurrentValue.RetainedHistoryEpochsCount;
            if (daysToKeep <= 0) daysToKeep = 30; // Global fallback count limit

            int nodeDaysToKeep = _storageSettings.CurrentValue.RetainedNodeHistoryEpochs;
            if (nodeDaysToKeep <= 0) nodeDaysToKeep = 7; // Per-node floor bound

            var availableDays = GetAvailableHistoricalDays(); // Sorted Descending (newest first)

            // Step 1: Group all JSON files found under peers subdirectories by NodeId
            var peerFilesByNode = new Dictionary<string, List<(string DayKey, string FilePath)>>(StringComparer.OrdinalIgnoreCase);

            foreach (var day in availableDays)
            {
                string peersDir = Path.Combine(_historyDir, day, "peers");
                if (Directory.Exists(peersDir))
                {
                    var files = Directory.GetFiles(peersDir, "*.json");
                    foreach (var file in files)
                    {
                        string nodeId = Path.GetFileNameWithoutExtension(file);
                        if (!peerFilesByNode.TryGetValue(nodeId, out var list))
                        {
                            list = new List<(string DayKey, string FilePath)>();
                            peerFilesByNode[nodeId] = list;
                        }
                        list.Add((day, file));
                    }
                }
            }

            // Step 2: Prune old peer JSON records safely
            int deletedCount = 0;
            foreach (var kvp in peerFilesByNode)
            {
                string nodeId = kvp.Key;
                var historyList = kvp.Value.OrderByDescending(x => x.DayKey).ToList();

                // If a node has fewer or equal active records than our node history floor target,
                // we retain ALL of them to protect critical handshake/verification credentials.
                if (historyList.Count <= nodeDaysToKeep)
                {
                    continue;
                }

                // Any records beyond the node floor target are evaluated against the global daysToKeep window
                for (int i = nodeDaysToKeep; i < historyList.Count; i++)
                {
                    var item = historyList[i];

                    // Find if this record falls outside the global daysToKeep window index
                    int dayIndex = availableDays.IndexOf(item.DayKey);
                    if (dayIndex >= daysToKeep)
                    {
                        try
                        {
                            if (File.Exists(item.FilePath))
                            {
                                File.Delete(item.FilePath);
                                deletedCount++;
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "[Consensus Store Pruner] Failed to delete outdated peer file: {Path}", item.FilePath);
                        }
                    }
                }
            }

            _logger.LogInformation("[Consensus Store Pruner] Cleaned up {Count} stale node history epochs, leaving key-handshake footprints intact.", deletedCount);

            // Step 3: Delete daily directories that have become empty
            foreach (var day in availableDays)
            {
                string dayPath = Path.Combine(_historyDir, day);
                if (Directory.Exists(dayPath))
                {
                    string peersPath = Path.Combine(dayPath, "peers");
                    bool hasActivePeers = Directory.Exists(peersPath) && Directory.GetFiles(peersPath, "*.json").Length > 0;

                    if (!hasActivePeers)
                    {
                        try
                        {
                            Directory.Delete(dayPath, true);
                            _logger.LogInformation("[Consensus Store Pruner] Wiped empty historical consensus epoch directory: {Dir}", day);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "[Consensus Store Pruner] Failed to wipe empty day directory: {Path}", dayPath);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Consensus Store Pruner] Failed executing target snapshot collection cleanup sweep loop.");
        }

        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _settingsChangeListener?.Dispose();
        _settingsChangeListener2?.Dispose();
    }
}