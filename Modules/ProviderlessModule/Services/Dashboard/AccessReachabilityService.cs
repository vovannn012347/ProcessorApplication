using System.Diagnostics;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using ProviderlessModule.Configuration;
using ProviderlessModule.DashboardWidgets.Widgets;
using ProviderlessModule.Infrastructure.Interfaces;
using ProviderlessModule.Services.Dashboard;

namespace ProviderlessModule.Services;

/// <summary>
/// Self-heartbeat: calls this node's own public URL (tunnel URL + /api/Heartbeat) to check that it is reachable from
/// outside, and keeps the dashboard access widget up to date. Switched on/off, logged and timed in
/// Providerless Settings -> Core Connectivity (SelfCheckEnabled / SelfCheckLogFailuresOnly / SelfCheckIntervalSeconds).
/// </summary>
public class AccessReachabilityService : BackgroundService
{
    // Own client, not IHttpClientFactory: the factory logs four "Start processing / Received HTTP response" lines
    // per request, i.e. per self-check, at Information. What gets logged is decided below.
    private static readonly HttpClient SelfCheckClient = new() { Timeout = TimeSpan.FromSeconds(5) };

    private readonly AccessStatusTracker _tracker;
    private readonly ITunnelSelector _tunnelSelector;
    private readonly IRegistrySelector _registrySelector;
    private readonly IOptionsMonitor<PortalAccessSettings> _settings;
    private readonly ILogger<AccessReachabilityService> _logger;

    public AccessReachabilityService(
        AccessStatusTracker tracker,
        ITunnelSelector tunnelSelector,
        IRegistrySelector registrySelector,
        IOptionsMonitor<PortalAccessSettings> settings,
        ILogger<AccessReachabilityService> logger)
    {
        _tracker = tracker;
        _tunnelSelector = tunnelSelector;
        _registrySelector = registrySelector;
        _settings = settings;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var settings = _settings.CurrentValue;
            var tunnel = _tunnelSelector.GetActiveProvider();
            var registry = _registrySelector.GetActiveRegistry();

            _tracker.TunnelState = tunnel.IsRunning ? 0 : 1;
            _tracker.RegistryState = registry.IsActive ? 0 : 1;

            string? baseUrl = tunnel.CurrentUrl;
            _tracker.CurrentExposedUrl = baseUrl ?? "None";

            if (!settings.SelfCheckEnabled)
            {
                _tracker.ReachabilityState = 1; // not checked
            }
            else if (string.IsNullOrEmpty(baseUrl))
            {
                _tracker.ReachabilityState = 1; // no public URL yet: nothing to check
            }
            else
            {
                await CheckAsync($"{baseUrl.TrimEnd('/')}/api/Heartbeat", settings.SelfCheckLogFailuresOnly, stoppingToken);
            }

            _tracker.LastCheckTime = DateTime.Now;

            int seconds = settings.SelfCheckIntervalSeconds >= 5 ? settings.SelfCheckIntervalSeconds : 30;
            await Task.Delay(TimeSpan.FromSeconds(seconds), stoppingToken);
        }
    }

    private async Task CheckAsync(string testUrl, bool logFailuresOnly, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, testUrl);
            request.Headers.Add("X-Internal-Ping", "true"); // the presence middleware ignores it

            using var response = await SelfCheckClient.SendAsync(request, ct);
            watch.Stop();

            if (response.IsSuccessStatusCode)
            {
                _tracker.ReachabilityState = 0;
                if (!logFailuresOnly)
                {
                    _logger.LogInformation("[Self-heartbeat] {Url} reachable ({Status}) in {Ms} ms.", testUrl, (int)response.StatusCode, watch.ElapsedMilliseconds);
                }
            }
            else
            {
                _tracker.ReachabilityState = 2;
                _logger.LogWarning("[Self-heartbeat] {Url} answered {Status} after {Ms} ms: this node is not reachable from outside.",
                    testUrl, (int)response.StatusCode, watch.ElapsedMilliseconds);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _tracker.ReachabilityState = 2;
            _logger.LogWarning("[Self-heartbeat] {Url} not reachable after {Ms} ms: {Error}", testUrl, watch.ElapsedMilliseconds, ex.GetBaseException().Message);
        }
    }
}
