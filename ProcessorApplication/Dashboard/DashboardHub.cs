using System.Reflection;
using System.Runtime.Loader;

using Common;
using Common.Models;

using Microsoft.AspNetCore.SignalR;

namespace ProcessorApplication.Dashboard;

// Active session tracking
public class DashboardHub : Hub
{
    private readonly IDashboardSessionManager _sessionManager;

    public DashboardHub(IDashboardSessionManager sessionManager)
    {
        _sessionManager = sessionManager;
    }

    public override async Task OnConnectedAsync()
    {
        _sessionManager.Heartbeat(Context.User.Identity.Name);
        await base.OnConnectedAsync();
    }

    /// <summary>
    /// Global lightweight heartbeat to keep the DashboardSession alive.
    /// </summary>
    public void Heartbeat()
    {
        _sessionManager.Heartbeat(Context.User.Identity.Name);
    }

    // JS calls: connection.invoke("ActivateWidget", "processor-logs")
    public void ActivateWidget(string widgetId)
    {
        _sessionManager.ActivateWidget(Context.User.Identity.Name, widgetId);
    }

    public override async Task OnDisconnectedAsync(System.Exception exception)
    {
        _sessionManager.RemoveSession(Context.User.Identity.Name);
        await base.OnDisconnectedAsync(exception);
    }
}