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
/// Advertises the node's endpoints automatically: every few seconds it asks every module's IEndpointProvider for its
/// endpoints (all of its sources), and when the node's advertised list changes (a tunnel came up, its URL changed or it
/// went down, or the static P2P Endpoints setting was edited) it re-announces the identity to the mesh.
/// Static endpoints from P2PSettings.Endpoints (e.g. a public IP on a hosted server) always come first and stay.
/// Off with P2PSettings.AutoAdvertiseEndpoints = false (then only the static list is advertised).
/// </summary>
public sealed class EndpointAdvertiserService : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IDynamicEndpointStore _store;
    private readonly INodeIdentityService _identity;
    private readonly IOptionsMonitor<P2PSettings> _settings;
    private readonly ILogger<EndpointAdvertiserService> _logger;

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

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    _store.Set(_settings.CurrentValue.AutoAdvertiseEndpoints ? CollectProvidedEndpoints() : Array.Empty<string>());

                    var advertised = _identity.GetEndpoints();
                    bool changed = !advertised.SequenceEqual(_lastAdvertised, StringComparer.OrdinalIgnoreCase);

                    // Wait for the identity to exist before announcing anything
                    if (changed && !string.IsNullOrEmpty(_identity.NodeId))
                    {
                        _logger.LogInformation("[P2P Endpoints] Advertised endpoints changed: [{Old}] -> [{New}]. Re-announcing identity.",
                            string.Join(", ", _lastAdvertised), string.Join(", ", advertised));
                        _lastAdvertised = advertised;
                        _identity.TriggerUpdate();
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[P2P Endpoints] Failed to refresh advertised endpoints.");
                }

                await Task.Delay(PollInterval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
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
