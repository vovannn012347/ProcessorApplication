using System.Collections.Concurrent;
using System.Text.Json;

using Microsoft.Extensions.Logging;

using P2PModule.Configuration;
using P2PModule.Interfaces;
using P2PModule.Models;

namespace P2PModule.Services.Rating;


public interface IPeerRatingOverrideService
{
    Task LoadRulesAsync();
    Task ApplyOverrideAsync(PeerOverrideRule rule);
    Task RemoveOverrideAsync(string nodeId);
    double OverridenScore(string nodeId, double systemCalculatedScore);
    IEnumerable<PeerOverrideRule> GetRules();
}

/// <summary>
/// Production-grade implementation of administrative trust overrides.
/// Persists overrides to local storage and intercepts core P2P evaluation pipelines.
/// </summary>
public class PeerRatingOverrideService : IPeerRatingOverrideService
{
    private readonly string _storagePath;
    private readonly ILogger<PeerRatingOverrideService> _logger;
    private readonly ConcurrentDictionary<string, PeerOverrideRule> _rules = new();

    public PeerRatingOverrideService(ILogger<PeerRatingOverrideService> logger)
    {
        _logger = logger;
        _storagePath = Path.Combine(AppContext.BaseDirectory, "data", "p2p", "overrides.json");
    }

    public async Task LoadRulesAsync()
    {
        if (!File.Exists(_storagePath)) return;

        try
        {
            var json = await File.ReadAllTextAsync(_storagePath);
            var list = JsonSerializer.Deserialize<List<PeerOverrideRule>>(json);
            if (list != null)
            {
                _rules.Clear();
                foreach (var item in list)
                {
                    _rules[item.NodeId] = item;
                }
            }
            _logger.LogInformation("[P2P Override] Loaded {Count} administrative routing override rules.", _rules.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[P2P Override] Failed to read overrides file on start.");
        }
    }

    public async Task ApplyOverrideAsync(PeerOverrideRule rule)
    {
        if (string.IsNullOrEmpty(rule.NodeId)) return;

        _rules[rule.NodeId] = rule;
        await SaveRulesAsync();
        _logger.LogWarning("[P2P Override] Admin applied trust rule for {NodeId}. Force Whitelist: {W}, Force Blacklist: {B}",
            rule.NodeId, rule.ForceWhitelist, rule.ForceBlacklist);
    }

    public async Task RemoveOverrideAsync(string nodeId)
    {
        if (_rules.TryRemove(nodeId, out _))
        {
            await SaveRulesAsync();
            _logger.LogInformation("[P2P Override] Administrative rule removed for node: {NodeId}", nodeId);
        }
    }

    public double OverridenScore(string nodeId, double systemCalculatedScore)
    {
        if (_rules.TryGetValue(nodeId, out var rule))
        {
            if (rule.ForceBlacklist)
            {
                return -50;
            }

            if (rule.ForceWhitelist)
            {
                return 100; // Elevate to max rating bypass
            }

            if (rule.OverrideTrustScore.HasValue)
            {
                return Math.Clamp(rule.OverrideTrustScore.Value, -50, 100);
            }
        }

        // Default automated baseline evaluation gate
        return systemCalculatedScore;
    }

    public IEnumerable<PeerOverrideRule> GetRules() => _rules.Values;

    private async Task SaveRulesAsync()
    {
        try
        {
            var dir = Path.GetDirectoryName(_storagePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var json = JsonSerializer.Serialize(new List<PeerOverrideRule>(_rules.Values), new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(_storagePath, json);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[P2P Override] Failed to write rules back to local storage.");
        }
    }
}