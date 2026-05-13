using ProviderlessModule.DashboardWidgets.Widgets;

namespace ProviderlessModule.Services.Dashboard;

/// <summary>
/// Singleton store to share state between the Orchestrator/Background pinger 
/// and the Dashboard Widget.
/// </summary>

public class AccessStatusTracker
{
    private readonly object _lock = new object();

    private int _registryState = 1;
    private int _tunnelState = 1;
    private int _reachabilityState = 1;
    private string _currentExposedUrl = "None";
    private DateTime _lastCheckTime = DateTime.MinValue;

    public int RegistryState
    {
        get { lock (_lock) return _registryState; }
        set { lock (_lock) _registryState = value; }
    }

    public int TunnelState
    {
        get { lock (_lock) return _tunnelState; }
        set { lock (_lock) _tunnelState = value; }
    }

    public int ReachabilityState
    {
        get { lock (_lock) return _reachabilityState; }
        set { lock (_lock) _reachabilityState = value; }
    }

    public string CurrentExposedUrl
    {
        get { lock (_lock) return _currentExposedUrl; }
        set { lock (_lock) _currentExposedUrl = value; }
    }

    public DateTime LastCheckTime
    {
        get { lock (_lock) return _lastCheckTime; }
        set { lock (_lock) _lastCheckTime = value; }
    }
}