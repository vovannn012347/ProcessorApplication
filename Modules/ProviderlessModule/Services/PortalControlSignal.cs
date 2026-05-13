using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using ProviderlessModule.Configuration;
using ProviderlessModule.Infrastructure;
using ProviderlessModule.Services.Registry;

namespace ProviderlessModule.Services;

public interface IPortalControlSignal
{
    void RequestRestart();
    Task WaitAsync(TimeSpan timeout, CancellationToken ct);
}

public class PortalControlSignal : IPortalControlSignal
{
    private readonly SemaphoreSlim _signal = new(0);

    public void RequestRestart()
    {
        // Releases the orchestrator if it's currently "waiting"
        if (_signal.CurrentCount == 0) _signal.Release();
    }

    public async Task WaitAsync(TimeSpan timeout, CancellationToken ct)
    {
        // Waits for either the timeout OR a manual trigger
        try { await _signal.WaitAsync(timeout, ct); }
        catch (OperationCanceledException) { /* Standard shutdown */ }
    }
}