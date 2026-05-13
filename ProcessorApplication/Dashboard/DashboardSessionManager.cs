using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.Loader;

using Common;
using Common.Models;

namespace ProcessorApplication.Dashboard;

public interface IDashboardSessionManager
{
    // inform that those session widgets are alive
    void Heartbeat(string userId);
    DashboardSession GetSession(string userId);
    void RemoveSession(string userId);
    void ActivateWidget(string name, string widgetId);
}

public class DashboardSessionManager : BackgroundService, IDashboardSessionManager
{
    private readonly ConcurrentDictionary<string, DashboardSession> _sessions = new();
    private readonly TimeSpan _timeout = TimeSpan.FromSeconds(45);

    // Updates timestamp only
    public void Heartbeat(string userId)
    {
        if (string.IsNullOrEmpty(userId)) return;
        var session = _sessions.GetOrAdd(userId, id => new DashboardSession { UserId = id });
        session.LastHeartbeat = DateTime.UtcNow;
    }

    // Adds widget to active set without clearing others
    public void ActivateWidget(string userId, string widgetId)
    {
        var session = _sessions.GetOrAdd(userId, id => new DashboardSession { UserId = id });
        lock (session.ActiveWidgetIds)
        {
            session.ActiveWidgetIds.Add(widgetId);
        }
        session.LastHeartbeat = DateTime.UtcNow;
    }

    public DashboardSession GetSession(string userId) =>
        _sessions.TryGetValue(userId, out var s) ? s : null;

    public void RemoveSession(string userId)
    {
        if (_sessions.TryRemove(userId, out var session))
        {
            foreach (var action in session.CleanupActions) try { action(); } catch { }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var now = DateTime.UtcNow;
            foreach (var s in _sessions.Values.ToList())
            {
                if (now - s.LastHeartbeat > _timeout) RemoveSession(s.UserId);
            }
            await Task.Delay(10000, stoppingToken);
        }
    }
}