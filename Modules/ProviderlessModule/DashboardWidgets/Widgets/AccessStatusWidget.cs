using System;
using System.Net;
using System.Security.Cryptography;
using System.Text;

using Common.Interfaces;

using ProviderlessModule.Services.Dashboard;

namespace ProviderlessModule.DashboardWidgets.Widgets;

public class AccessStatusWidget : IDashboardWidget, IDisposable
{
    private readonly AccessStatusTracker _tracker;

    public AccessStatusWidget(AccessStatusTracker tracker)
    {
        _tracker = tracker;
    }

    public WidgetManifest Manifest => new WidgetManifest
    {
        Id = "access-status",
        Name = "Connectivity Hub",
        IconClass = "fa-solid fa-cloud-arrow-up",
        Roles = "Admin,Registrature",
        ViewPath = "~/Views/Providerless/DashboardWidgets/_AccessStatus.cshtml",
        ScriptPath = "/Providerless/js/dashboard/widgets/access-widget.js"
    };

    public Task<object> GetUpdateAsync()
    {
        return Task.FromResult<object>(new
        {
            registry = _tracker.RegistryState,
            tunnel = _tracker.TunnelState,
            reachability = _tracker.ReachabilityState,
            url = _tracker.CurrentExposedUrl,
            lastChecked = _tracker.LastCheckTime.ToString("HH:mm:ss")
        });
    }

    public void Dispose() { }
}