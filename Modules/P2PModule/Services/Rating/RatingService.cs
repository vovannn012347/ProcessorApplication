using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using P2PModule.Configuration;
using P2PModule.Interfaces;
using P2PModule.Services.Gossip;
using P2PModule.Services.Persistence;

namespace P2PModule.Services.Rating;



public interface IRatingService
{
    Task ApplyLocalReputationUpdateAsync(string nodeIdTo, double baseAmount, bool isTrapdoor = true);
    Task ApplyBurnBothEndsAccusationAsync(string accuserNodeId, string accusedNodeId, double accusedPenalty = -40.0, bool isTrapdoorFault = true);
    double GetEffectiveTrustScore(string nodeId, bool applyOverrides = true);
    bool IsConnectivityValid(string nodeId, bool applyOverrides = true);

    int GetNodeHistoricalSeniority(string nodeId);
    void PruneHistory(string nodeId);
}

/// <summary>
/// Minimalist reputation engine executing sub-linear logarithmic growth, 
/// instantaneous trapdoor penalties, seniority shielding, and chronological regressions.
/// </summary>
public class RatingService : IRatingService, IDisposable
{
    private readonly IPeerTracker _peerTracker;
    private readonly IPeerRatingOverrideService _overrideService;
    private readonly IOptionsMonitor<RatingSettings> _ratingSettings;
    private readonly ILogger<RatingService> _logger;
    private readonly ConcurrentDictionary<string, double> _localObservationMatrix = new();

    public RatingService(
        ILogger<RatingService> logger,
        IOptionsMonitor<RatingSettings> settings,
        IPeerTracker peerTracker,
        IPeerRatingOverrideService overrideService)
    {
        _logger = logger;
        _ratingSettings = settings;
        _peerTracker = peerTracker;
        _overrideService = overrideService;
    }

    /// <summary>
    /// Updates local reputation score.
    /// Supports both standard seniority-shielded updates (isTrapdoor = false)
    /// and unmitigated Byzantine/cryptographic fault drops (isTrapdoor = true).
    /// </summary>
    public async Task ApplyLocalReputationUpdateAsync(string nodeIdTo, double baseAmount, bool isTrapdoor = true)
    {
        if (string.IsNullOrEmpty(nodeIdTo)) return;

        double currentBaseScore = 0.0;
        DateTime lastUpdated = DateTime.UtcNow;

        var peerIndex = _peerTracker.GetPeerIndex(nodeIdTo);
        if (peerIndex != null)
        {
            currentBaseScore = peerIndex.TrustScore;
            lastUpdated = peerIndex.LastUpdate;
        }

        // 1. Apply asymmetric exponential decay up to current time
        double decayedScore = ApplyExponentialDecay(currentBaseScore, lastUpdated);

        // 2. Compute effective delta based on mode (growth dampening, shielded penalty, or raw trapdoor)
        double effectiveDelta = baseAmount;

        if (baseAmount > 0)
        {
            // Sub-linear diminishing growth: growth slows down as rating approaches max (100)
            double growthModifier = Math.Max(0.05, 1.0 - (Math.Max(0.0, decayedScore) / 100.0));
            effectiveDelta = baseAmount * growthModifier;
        }
        else if (baseAmount < 0 && !isTrapdoor)
        {
            // Standard negative penalty with Seniority Shielding
            int seniorityEpochs = GetNodeHistoricalSeniority(nodeIdTo);
            double smoothingCoeff = _ratingSettings.CurrentValue?.SenioritySmoothingCoefficient ?? 15.0;
            double seniorityShield = seniorityEpochs > 0 ? seniorityEpochs / (double)(seniorityEpochs + smoothingCoeff) : 0.0;

            // Shield dampens up to 50% of the penalty for highly senior nodes
            double penaltyShieldModifier = 1.0 - (0.5 * seniorityShield);
            effectiveDelta = baseAmount * penaltyShieldModifier;
        }
        // Note: If baseAmount < 0 and isTrapdoor == true, effectiveDelta remains unmitigated baseAmount

        double localRating = Math.Clamp(decayedScore + effectiveDelta, -50.0, 100.0);

        await _peerTracker.UpdateTrustAsync(nodeIdTo, localRating);

        _logger.LogInformation("[RatingService] Updated Node {NodeId}: BaseAmount={BaseAmount} (Effective={EffectiveDelta:F2}, Trapdoor={IsTrapdoor}). Prev={Prev}, Decayed={Decayed}, NewScore={NewScore}",
            nodeIdTo, baseAmount, effectiveDelta, isTrapdoor, currentBaseScore, decayedScore, localRating);
    }

    /// <summary>
    /// Executes Burn Both Ends protocol: The Accused receives a penalty (trapdoor by default for cryptographic faults),
    /// while the Accuser pays a light, non-trapdoor collateral stake burn.
    /// </summary>
    public async Task ApplyBurnBothEndsAccusationAsync(string accuserNodeId, string accusedNodeId, double accusedPenalty = -40.0, bool isTrapdoorFault = true)
    {
        if (string.IsNullOrEmpty(accusedNodeId)) return;

        // 1. Dock the Accused node
        await ApplyLocalReputationUpdateAsync(accusedNodeId, accusedPenalty, isTrapdoor: isTrapdoorFault);

        // 2. Dock the Accuser node (always isTrapdoor = false so the accuser only pays a light, seniority-shielded stake tax)
        if (!string.IsNullOrEmpty(accuserNodeId) && !string.Equals(accuserNodeId, accusedNodeId, StringComparison.OrdinalIgnoreCase))
        {
            double baseBurn = -2.0; // Light stake tax
            await ApplyLocalReputationUpdateAsync(accuserNodeId, baseBurn, isTrapdoor: false);
        }
    }

    /// <summary>
    /// Computes effective trust score applying exponential decay up to DateTime.UtcNow 
    /// and evaluates administrative overrides.
    /// Synchronizes runtime peer cache in PeerTracker so UI and query states remain aligned.
    /// </summary>
    public double GetEffectiveTrustScore(string nodeId, bool applyOverrides = true)
    {
        double localRating = 0.0;
        var peerIndex = _peerTracker.GetPeerIndex(nodeId);
        if (peerIndex != null)
        {
            localRating = ApplyExponentialDecay(peerIndex.TrustScore, peerIndex.LastUpdate);
        }

        // Synchronize in-memory runtime peer cache with actualized decayed trust score
        var runtimePeer = _peerTracker.GetPeer(nodeId, historic: false);
        if (runtimePeer != null)
        {
            runtimePeer.TrustScore = applyOverrides
                ? _overrideService.OverridenScore(nodeId, localRating)
                : localRating;
        }

        if (applyOverrides)
        {
            return _overrideService.OverridenScore(nodeId, localRating);
        }

        return localRating;
    }

    /// <summary>
    /// Calculates exponential rating decay/indulgence based on days passed since last update.
    /// </summary>
    private double ApplyExponentialDecay(double currentScore, DateTime lastUpdatedUtc)
    {
        double daysPassed = (DateTime.UtcNow - lastUpdatedUtc).TotalDays;
        if (daysPassed <= 0) return currentScore;

        var settings = _ratingSettings.CurrentValue;

        if (currentScore > 0)
        {
            double decayRate = settings?.PositiveTrustDecayRate ?? 0.05;
            return currentScore * Math.Exp(-decayRate * daysPassed);
        }
        else if (currentScore < 0)
        {
            double indulgenceRate = settings?.NegativeTrustIndulgenceRate ?? 0.25;
            return currentScore * Math.Exp(-indulgenceRate * daysPassed);
        }

        return 0.0;
    }

    /// <summary>
    /// Validates whether a node has enough effective trust to participate in WebRTC tunnels or signaling streams,
    /// enforcing administrative whitelist and blacklist gates.
    /// </summary>
    public bool IsConnectivityValid(string nodeId, bool applyOverrides = true)
    {
        double effectiveScore = GetEffectiveTrustScore(nodeId, applyOverrides);

        if (applyOverrides)
        {
            // Delegate connectivity permission directly to override service rules (ForceWhitelist/ForceBlacklist/Default checks)
            effectiveScore = _overrideService.OverridenScore(nodeId, effectiveScore);
        }

        double minimumTrust = _ratingSettings.CurrentValue?.MinimumTrustThreshold ?? -10.0;
        return effectiveScore >= minimumTrust;
    }

    /// <summary>
    /// Extracts the integer count of distinct historical days this node identity has been verified on disk.
    /// </summary>
    public int GetNodeHistoricalSeniority(string nodeId)
    {
        var peerIndex = _peerTracker.GetPeerIndex(nodeId);
        if (peerIndex == null) return 0;

        return peerIndex.IndexEpochs.Count();
    }

    public void PruneHistory(string nodeId)
    {
        _localObservationMatrix.TryRemove(nodeId, out _);
    }

    public void Dispose()
    {
        _localObservationMatrix.Clear();
    }
}