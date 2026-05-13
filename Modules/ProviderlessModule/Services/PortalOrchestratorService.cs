using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using ProviderlessModule.Configuration;
using ProviderlessModule.Infrastructure.Interfaces;
using ProviderlessModule.Services.Registry;

namespace ProviderlessModule.Services;

/// <summary>
/// Orchestrates the startup sequence: Server Warmup -> Port Discovery -> Tunnel Launch.
/// </summary>
public class PortalOrchestratorService : BackgroundService
{
    private readonly IBinaryBootstrapper _bootstrapper;
    private readonly ITunnelSelector _tunnelSelector; 
    private readonly IRegistrySelector _registrySelector;
    private readonly ILocalDataProvider _localData;      
    private readonly PortalState _state;
    private readonly IOptionsMonitor<PortalAccessSettings> _settings;
    private readonly ILogger<PortalOrchestratorService> _logger; 
    private readonly IPortalControlSignal _controlSignal;

    // New dependencies for dynamic port discovery
    private readonly IServer _server;
    private readonly IHostApplicationLifetime _lifetime;

    public PortalOrchestratorService(
        IBinaryBootstrapper bootstrapper,
        ITunnelSelector tunnelSelector, 
        IRegistrySelector registrySelector,
        ILocalDataProvider localData,
        PortalState state,
        IOptionsMonitor<PortalAccessSettings> settings,
        ILogger<PortalOrchestratorService> logger,
        IServer server,
        IHostApplicationLifetime lifetime,
        IPortalControlSignal controlSignal)
    {
        _bootstrapper = bootstrapper;
        _tunnelSelector = tunnelSelector; 
        _registrySelector = registrySelector;
        _localData = localData;
        _state = state;
        _settings = settings;
        _logger = logger;
        _server = server;
        _lifetime = lifetime;
        _controlSignal = controlSignal;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _logger.LogInformation("Portal Connectivity Orchestrator is online.");

        try
        {
            // STEP 0: Warmup - Wait for Kestrel to pick a port
            var tcs = new TaskCompletionSource();
            using var reg = _lifetime.ApplicationStarted.Register(() => tcs.SetResult());
            await tcs.Task;

            _logger.LogInformation("Kestrel ready. Entering lifecycle management loop...");

            while (!ct.IsCancellationRequested)
            {
                var settings = _settings.CurrentValue;
                var tunnel = _tunnelSelector.GetActiveProvider();

                if (tunnel.IsRunning && _state.RestartRequested)
                {
                    _logger.LogWarning("Manual restart signal received. Tearing down tunnel...");
                    await tunnel.StopTunnelAsync();
                    _state.IsActive = false;
                    _state.RestartRequested = false; // Reset the flag
                }

                // CASE 1: DISABLED - If setting is OFF but tunnel is ON, kill it.
                if (!settings.Enabled)
                {
                    if (tunnel.IsRunning)
                    {
                        _logger.LogWarning("Portal access disabled via settings. Shutting down active tunnel...");
                        await tunnel.StopTunnelAsync();

                        _state.IsActive = false;
                        _state.CurrentTunnelUrl = null;
                    }

                    // Idle wait: Check again in 10 seconds to see if user re-enabled it.
                    await Task.Delay(TimeSpan.FromSeconds(10), ct);
                    continue;
                }

                // CASE 2: ENABLED BUT DOWN - Start or Recover
                if (!tunnel.IsRunning)
                {
                    try
                    {
                        _logger.LogInformation("Portal access enabled. Starting {Provider} sequence...", tunnel.Provider);

                        // 1. Ensure Binaries
                        await _bootstrapper.EnsureBinariesAsync(ct);

                        // 2. Discover Port
                        var addressFeature = _server.Features.Get<IServerAddressesFeature>();
                        int port = 5000;
                        string scheme = "https";
                        if (Uri.TryCreate(addressFeature?.Addresses.FirstOrDefault(), UriKind.Absolute, out var uri))
                        {
                            port = uri.Port; 
                            scheme = uri.Scheme;
                        }

                        // 3. Start Tunnel (This handles Provisioning/Sync internally)
                        string publicUrl = await tunnel.StartTunnelAsync(port, scheme, ct);

                        // 4. Update State
                        _state.CurrentTunnelUrl = publicUrl;
                        _state.IsActive = true;
                        _state.TunnelStartedAt = DateTime.UtcNow;

                        var registry = _registrySelector.GetActiveRegistry();
                        string clinicId = _localData.GetRegistryAlias();

                        _logger.LogInformation("Registering discovery via {RegistryProvider}...", registry.Provider);
                        await registry.RegisterAccessAsync(publicUrl, ct);

                        _logger.LogInformation("SUCCESS: Portal accessible and registered at {Url}", publicUrl);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError("Startup/Recovery failed. Retry in 30s. Error: {Msg}", ex.Message);
                        _state.IsActive = false;
                        _state.LastError = ex.Message;
                    }
                }
                else
                {
                    // HEARTBEAT CHECK:
                    // If the tunnel is running, we still call RegisterAccessAsync.
                    // The GitHubRegistry implementation handles the interval-based update (idempotency).
                    try
                    {
                        var registry = _registrySelector.GetActiveRegistry();
                        await registry.RegisterAccessAsync(
                            _state.CurrentTunnelUrl!, 
                            ct);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning("Heartbeat update failed: {Msg}", ex.Message);
                    }
                }

                // CASE 3: HEALTHY - Standard 30s heart-beat delay
                await _controlSignal.WaitAsync(TimeSpan.FromSeconds(30), ct);
            }
        }
        catch (OperationCanceledException) { _logger.LogInformation("Orchestrator shutting down."); }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Orchestrator encountered a fatal failure.");
            _state.IsActive = false;
        }
    }
}