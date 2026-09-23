// [full-implementation]
using System.Collections.Concurrent;
using System.Data;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

using Common.Interfaces;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using P2PModule.Configuration;
using P2PModule.Infrastructure;
using P2PModule.Interfaces;
using P2PModule.Models;
using P2PModule.Models.Gossips;
using P2PModule.Models.Network;
using P2PModule.Services.Connection;
using P2PModule.Services.Gossip;
using P2PModule.Services.Persistence;

namespace P2PModule.Services.Maintenance;
/// <summary>
/// Background timer service responsible for triggering daily epidemic consensus sessions.
/// Incorporates randomized startup jitter delays to stagger outbound network fan-outs across peers
/// and mitigate network-wide "consensus storm" connection spikes.
/// </summary>
public class ConsensusInitiatorHostedService : BackgroundService
{
    private readonly IConsensusProtocolHandler _protocolHandler;
    private readonly IConsensusSessionManager _sessionManager;
    private readonly IOptionsMonitor<P2PSettings> _p2pSettings;
    private readonly ILogger<ConsensusInitiatorHostedService> _logger;

    private string _lastExecutedDateKey = string.Empty;
    private const int TargetScheduledHourUtc = 23; // Default 23:00 UTC daily consensus window
    private const int MaxRandomJitterMinutes = 5; // Stagger startups up to 5 minutes

    public ConsensusInitiatorHostedService(
        IConsensusProtocolHandler protocolHandler,
        IConsensusSessionManager sessionManager,
        IOptionsMonitor<P2PSettings> p2pSettings,
        ILogger<ConsensusInitiatorHostedService> logger)
    {
        _protocolHandler = protocolHandler;
        _sessionManager = sessionManager;
        _p2pSettings = p2pSettings;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[Consensus Initiator] Starting daily scheduled consensus monitor.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var nowUtc = DateTime.UtcNow;
                string todayKey = nowUtc.ToString("yyyy-MM-dd");

                // Check if we are inside the scheduled hour window and haven't triggered consensus for today
                if (nowUtc.Hour == TargetScheduledHourUtc && !string.Equals(_lastExecutedDateKey, todayKey, StringComparison.OrdinalIgnoreCase))
                {
                    // Check if an incoming ping or manual trigger already booted a session
                    if (_sessionManager.IsBusy())
                    {
                        _logger.LogInformation("[Consensus Initiator] Consensus session for {DateKey} is already running (triggered via ping or manual override). Skipping scheduled launch.", todayKey);
                        _lastExecutedDateKey = todayKey;
                    }
                    else
                    {
                        await ExecuteStaggeredConsensusLaunchAsync(todayKey, stoppingToken);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                _logger.LogInformation("[Consensus Initiator] Service stopping due to cancellation request.");
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Consensus Initiator] Exception occurred inside scheduled monitor loop.");
            }

            // Check clock status every 30 seconds
            await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);
        }
    }

    /// <summary>
    /// Calculates a random jitter delay before initiating consensus to spread out network traffic.
    /// If an incoming start ping initializes a session during the jitter window, the local launch is skipped.
    /// </summary>
    private async Task ExecuteStaggeredConsensusLaunchAsync(string todayKey, CancellationToken stoppingToken)
    {
        // Generate random jitter delay between 15 seconds and MaxRandomJitterMinutes
        var random = Random.Shared;
        int jitterSeconds = random.Next(15, MaxRandomJitterMinutes * 60);
        TimeSpan jitterDelay = TimeSpan.FromSeconds(jitterSeconds);

        _logger.LogInformation("[Consensus Initiator] Scheduled 23:00 UTC window reached for date {DateKey}. Applying random startup jitter of {Minutes:F1} minutes ({Seconds}s) to prevent consensus storm.",
            todayKey, jitterDelay.TotalMinutes, jitterSeconds);

        // Wait out the randomized jitter delay
        await Task.Delay(jitterDelay, stoppingToken);

        // Re-check after jitter delay: another node may have sent us a start ping while we were waiting!
        if (_sessionManager.IsBusy())
        {
            _logger.LogInformation("[Consensus Initiator] Active session was initialized during jitter delay by an incoming peer ping. Suppressing duplicate launch.");
            _lastExecutedDateKey = todayKey;
            return;
        }

        _logger.LogInformation("[Consensus Initiator] Jitter delay elapsed. Triggering active consensus workflow loop for {DateKey}...", todayKey);

        bool started = await _protocolHandler.TriggerConsensusAsync(stoppingToken);
        if (started)
        {
            _lastExecutedDateKey = todayKey;
            _logger.LogInformation("[Consensus Initiator] Consensus workflow loop successfully booted by scheduled initiator.");
        }
        else
        {
            _logger.LogWarning("[Consensus Initiator] Failed to acquire session locks for scheduled consensus launch on {DateKey}.", todayKey);
        }
    }
}