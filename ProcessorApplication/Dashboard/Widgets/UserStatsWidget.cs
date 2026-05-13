using Common.Interfaces;

using Infrastructure.Monitoring;

using Microsoft.EntityFrameworkCore;

using ProcessorApplication.Database;

namespace ProcessorApplication.Dashboard.Widgets;

public class UserStatsWidget : IDashboardWidget, IDisposable
{
    private readonly UserPresenceStore _presenceStore;
    private readonly IDbContextFactory<AppDbContext> _dbFactory;

    public UserStatsWidget(UserPresenceStore presenceStore, IDbContextFactory<AppDbContext> dbFactory)
    {
        _presenceStore = presenceStore;
        _dbFactory = dbFactory;
    }

    public WidgetManifest Manifest => new WidgetManifest
    {
        Id = "main-user-stats",
        Name = "Node Activity",
        IconClass = "fa-solid fa-users",
        Roles = "Admin,Registrature",
        ViewPath = "~/Views/DashboardWidgets/_UserStats.cshtml",
        ScriptPath = "/js/dashboard/widgets/users-widget.js"
    };

    public async Task<object> GetUpdateAsync()
    {
        try
        {
            using var db = await _dbFactory.CreateDbContextAsync();

            // 1. Get real-time active from Middleware Store (Active in last 5 mins)
            int activeCount = _presenceStore.GetActiveCount(TimeSpan.FromMinutes(5));

            // 2. Get total registered users from DB
            int totalRegistered = await db.Users.CountAsync();

            // State Logic: 0=Ok, 1=Warning (high load?), 2=Critical
            int state = 0;
            if (activeCount > 50) state = 1; // Example threshold

            return new
            {
                state = state,
                total = totalRegistered,
                active = activeCount,
                lastChecked = DateTime.Now.ToString("HH:mm:ss")
            };
        }
        catch
        {
            return new { state = 2, total = 0, active = 0 }; // Database unreachable
        }
    }

    public void Dispose() { }
}