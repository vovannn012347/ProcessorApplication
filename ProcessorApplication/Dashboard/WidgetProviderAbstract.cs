using System;
using System.Drawing;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

using Common.Interfaces;

namespace ProcessorApplication.Dashboard;

public abstract class WidgetProviderAbstract : IWidgetProvider
{
    protected readonly IServiceProvider _serviceProvider;
    protected readonly IDashboardSessionManager _sessionManager;

    public WidgetProviderAbstract(IServiceProvider serviceProvider, IDashboardSessionManager sessionManager)
    {
        _serviceProvider = serviceProvider;
        _sessionManager = sessionManager;
    }

    protected abstract void InitializeWidgetCaches();
    protected abstract Type GetWidget(string widgetId);
    public abstract bool HasWidget(string widgetId);
    public abstract IEnumerable<WidgetManifest> GetWidgetManifests();
    public IDashboardWidget GetWidget(string userId, string widgetId)
    {
        var session = _sessionManager.GetSession(userId);
        if (session == null) return null;

        // 1. Check if the instance already exists in session memory
        if (session.WidgetInstances.TryGetValue(widgetId, out var existing))
        {
            return (IDashboardWidget)existing;
        }

        // 2. Not in session - verify the type exists
        var type = GetWidget(widgetId);
        if (type == null)
        {
            return null;
        }

        // 3. Create fresh instance using ActivatorUtilities to satisfy DI dependencies
        var newInstance = (IDashboardWidget)ActivatorUtilities.CreateInstance(_serviceProvider, type);

        // 4. Bind to session for future updates
        if (session.WidgetInstances.TryAdd(widgetId, newInstance))
        {
            // Register cleanup if the widget manages timers, handles, or background tasks
            if (newInstance is IDisposable d)
            {
                session.CleanupActions.Add(() => d.Dispose());
            }
        }

        return newInstance;
    }
    public async Task<Dictionary<string, object>> GetUpdatesAsync(string userId, IEnumerable<string> widgetIds)
    {
        var results = new Dictionary<string, object>();

        foreach (var id in widgetIds)
        {
            // Use the new logic to get the "Living" instance
            var widget = GetWidget(userId, id);
            if (widget != null)
            {
                results[id] = await widget.GetUpdateAsync();
            }
        }

        return results;
    }
}