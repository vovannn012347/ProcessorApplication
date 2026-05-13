using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.Loader;

using Common;
using Common.Interfaces;
using Common.Models;

namespace ProcessorApplication.Dashboard;

// Active session tracking
public class DashboardSession
{
    public string UserId { get; set; }
    public DateTime LastHeartbeat { get; set; }
    public HashSet<string> ActiveWidgetIds { get; set; } = new();

    // Modules can attach cleanup callbacks here
    public List<Action> CleanupActions { get; set; } = new();

    /// <summary>
    /// Stores living IDashboardWidget instances. 
    /// These are created via DI and persist until the session is dropped.
    /// </summary>
    public ConcurrentDictionary<string, IDashboardWidget> WidgetInstances { get; set; } = new();
}