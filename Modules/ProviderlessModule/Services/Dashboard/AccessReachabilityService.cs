using Microsoft.Extensions.Hosting;

using ProviderlessModule.DashboardWidgets.Widgets;
using ProviderlessModule.Infrastructure.Interfaces;
using ProviderlessModule.Services.Dashboard;

namespace ProviderlessModule.Services;
public class AccessReachabilityService : BackgroundService
{
    private readonly AccessStatusTracker _tracker;
    private readonly ITunnelSelector _tunnelSelector;
    private readonly IRegistrySelector _registrySelector;
    private readonly IHttpClientFactory _httpClientFactory;

    public AccessReachabilityService(
        AccessStatusTracker tracker,
        ITunnelSelector tunnelSelector,
        IRegistrySelector registrySelector,
        IHttpClientFactory httpClientFactory)
    {
        _tracker = tracker;
        _tunnelSelector = tunnelSelector;
        _registrySelector = registrySelector;
        _httpClientFactory = httpClientFactory;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var tunnel = _tunnelSelector.GetActiveProvider();
            var registry = _registrySelector.GetActiveRegistry();

            _tracker.TunnelState = tunnel.IsRunning ? 0 : 1;
            _tracker.RegistryState = registry.IsActive ? 0 : 1;

            // Test target is now the non-redirecting Heartbeat API
            string baseUrl = tunnel.CurrentUrl;
            string testUrl = !string.IsNullOrEmpty(baseUrl) ? $"{baseUrl.TrimEnd('/')}/api/Heartbeat" : null;

            _tracker.CurrentExposedUrl = baseUrl ?? "None";

            if (!string.IsNullOrEmpty(testUrl))
            {
                try
                {
                    using var client = _httpClientFactory.CreateClient();
                    client.Timeout = TimeSpan.FromSeconds(5);

                    // Add header to ensure Middleware ignores this request
                    var request = new HttpRequestMessage(HttpMethod.Get, testUrl);
                    request.Headers.Add("X-Internal-Ping", "true");

                    var response = await client.SendAsync(request, stoppingToken);
                    _tracker.ReachabilityState = response.IsSuccessStatusCode ? 0 : 2;
                }
                catch
                {
                    _tracker.ReachabilityState = 2;
                }
            }
            else
            {
                _tracker.ReachabilityState = 1;
            }

            _tracker.LastCheckTime = DateTime.Now;
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
    }
}