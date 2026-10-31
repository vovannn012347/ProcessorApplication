using Common.Interfaces;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using P2PModule.Configuration;

namespace P2PModule.Services.Persistence;

/// <summary>
/// Endpoints that other modules provide at runtime (IEndpointProvider, e.g. ProviderlessModule's live tunnel URL).
/// Filled by <see cref="EndpointAdvertiserService"/>, read by NodeIdentityService.GetEndpoints().
/// </summary>
public interface IDynamicEndpointStore
{
    IReadOnlyList<string> Current { get; }

    /// <summary>Replaces the list. Returns true if it changed.</summary>
    bool Set(IReadOnlyList<string> endpoints);
}

public sealed class DynamicEndpointStore : IDynamicEndpointStore
{
    private readonly object _lock = new();
    private IReadOnlyList<string> _current = Array.Empty<string>();

    public IReadOnlyList<string> Current
    {
        get { lock (_lock) return _current; }
    }

    public bool Set(IReadOnlyList<string> endpoints)
    {
        lock (_lock)
        {
            if (_current.SequenceEqual(endpoints, StringComparer.OrdinalIgnoreCase)) return false;
            _current = endpoints.ToList();
            return true;
        }
    }
}

/// <summary>
/// Advertises the node's endpoints. It doesn't poll: modules call IEndpointChangeNotifier.NotifyEndpointsChanged() when
/// their IEndpointProvider output changes (a tunnel came up, its URL changed or it went down), and the P2P settings
/// change listener does the same for the static list. On a notification it re-reads all providers and, if the node's
/// advertised list differs from the last one announced, re-announces the identity to the mesh.
/// Static endpoints from P2PSettings.Endpoints (e.g. a public IP on a hosted server) always come first and stay.
/// Off with P2PSettings.AutoAdvertiseEndpoints = false (then only the static list is advertised).
/// </summary>
public sealed class EndpointAdvertiserService : BackgroundService, IEndpointChangeNotifier
{
    // While the identity doesn't exist yet, look again after this delay
    private static readonly TimeSpan IdentityWaitRetry = TimeSpan.FromSeconds(5);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IDynamicEndpointStore _store;
    private readonly INodeIdentityService _identity;
    private readonly IOptionsMonitor<P2PSettings> _settings;
    private readonly ILogger<EndpointAdvertiserService> _logger;

    // Coalesces notifications: several calls while a check is pending or running result in one more check
    private readonly SemaphoreSlim _signal = new(0, 1);
    private IDisposable? _settingsListener;

    private List<string> _lastAdvertised = new();

    public EndpointAdvertiserService(
        IServiceScopeFactory scopeFactory,
        IDynamicEndpointStore store,
        INodeIdentityService identity,
        IOptionsMonitor<P2PSettings> settings,
        ILogger<EndpointAdvertiserService> logger)
    {
        _scopeFactory = scopeFactory;
        _store = store;
        _identity = identity;
        _settings = settings;
        _logger = logger;
    }

    public void NotifyEndpointsChanged()
    {
        try { _signal.Release(); }
        catch (SemaphoreFullException) { /* a check is already pending */ }
        catch (ObjectDisposedException) { }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Static endpoints (or AutoAdvertiseEndpoints) edited in the P2P settings
        _settingsListener = _settings.OnChange(_ => NotifyEndpointsChanged());

        NotifyEndpointsChanged(); // first check at startup

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await _signal.WaitAsync(stoppingToken);

                try
                {
                    if (!RecheckAndAnnounce())
                    {
                        // Identity not created yet: check again shortly instead of losing this change
                        _ = Task.Delay(IdentityWaitRetry, stoppingToken)
                            .ContinueWith(_ => NotifyEndpointsChanged(), TaskContinuationOptions.OnlyOnRanToCompletion);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[P2P Endpoints] Failed to refresh advertised endpoints.");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>Returns false if the identity isn't ready yet (nothing announced).</summary>
    private bool RecheckAndAnnounce()
    {
        _store.Set(_settings.CurrentValue.AutoAdvertiseEndpoints ? CollectProvidedEndpoints() : Array.Empty<string>());

        var advertised = _identity.GetEndpoints();
        if (advertised.SequenceEqual(_lastAdvertised, StringComparer.OrdinalIgnoreCase)) return true;

        if (string.IsNullOrEmpty(_identity.NodeId)) return false;

        _logger.LogInformation("[P2P Endpoints] Advertised endpoints changed: [{Old}] -> [{New}]. Re-announcing identity.",
            string.Join(", ", _lastAdvertised), string.Join(", ", advertised));
        _lastAdvertised = advertised;
        _identity.TriggerUpdate();
        return true;
    }

    public override void Dispose()
    {
        _settingsListener?.Dispose();
        _signal.Dispose();
        base.Dispose();
    }

    private List<string> CollectProvidedEndpoints()
    {
        var result = new List<string>();

        using var scope = _scopeFactory.CreateScope();
        foreach (var provider in scope.ServiceProvider.GetServices<IEndpointProvider>())
        {
            IEnumerable<string> sources;
            try { sources = provider.GetEndpointSources() ?? Enumerable.Empty<string>(); }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[P2P Endpoints] {Provider} failed to list its sources.", provider.GetType().Name);
                continue;
            }

            foreach (var source in sources)
            {
                try
                {
                    foreach (var endpoint in provider.ProvideEndpoints(source) ?? Enumerable.Empty<string>())
                    {
                        string normalized = EndpointText.Normalize(endpoint);
                        if (normalized.Length > 0 && !result.Any(e => EndpointText.SameAuthority(e, normalized)))
                        {
                            result.Add(normalized);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "[P2P Endpoints] {Provider} failed to provide endpoints for {Source}.", provider.GetType().Name, source);
                }
            }
        }

        return result;
    }
}

internal static class EndpointText
{
    /// <summary>Trims whitespace and trailing slashes. The scheme is kept if the endpoint has one.</summary>
    public static string Normalize(string? endpoint) => (endpoint ?? string.Empty).Trim().TrimEnd('/');

    /// <summary>Same host:port (and path), ignoring the scheme and letter case.</summary>
    public static bool SameAuthority(string a, string b) =>
        string.Equals(StripScheme(Normalize(a)), StripScheme(Normalize(b)), StringComparison.OrdinalIgnoreCase);

    private static string StripScheme(string value)
    {
        int i = value.IndexOf("://", StringComparison.Ordinal);
        return i >= 0 ? value[(i + 3)..] : value;
    }
}
