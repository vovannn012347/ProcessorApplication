using System;
using System.Net;
using System.Security.Cryptography;
using System.Text;

using Common.Interfaces;

using Microsoft.EntityFrameworkCore;

using ProcessingModule.Database;

namespace ProcessingModule.DashboardWidgets.Widgets;

public class ProcessingStatusWidget : IDashboardWidget, IDisposable
{
    private readonly IDbContextFactory<ProcessorDbContext> _dbFactory;

    public ProcessingStatusWidget(IDbContextFactory<ProcessorDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public WidgetManifest Manifest => new WidgetManifest
    {
        Id = "process-status",
        Name = "Compute Engine",
        IconClass = "fa-solid fa-microchip",
        Roles = "Admin,ProcessRunner",
        ViewPath = $"~/Views/{ProcessorModule.MODULE_ID}/DashboardWidgets/_ProcessStatus.cshtml",
        ScriptPath = "/Processing/js/dashboard/widgets/process-widget.js"
    };

    public async Task<object> GetUpdateAsync()
    {
        try
        {
            using var db = await _dbFactory.CreateDbContextAsync();

            // 1. Scripts count
            var scriptCount = await db.Scripts.CountAsync();

            // 2. Active tasks (Status: Running)
            var activeTasks = await db.ProcessingJobs.CountAsync(j => j.Status == "Running");

            // 3. Total jobs
            var totalJobs = await db.Jobs.CountAsync();

            // 4. Jobs today
            var today = DateTime.UtcNow.Date;
            var jobsToday = await db.Jobs.CountAsync(j => j.CreatedTime >= today);

            // Determine States (0=Ok, 1=Warning/Busy, 2=Error)
            int engineState = 0; // Assume OK if we reached this line
            int indexState = scriptCount > 0 ? 0 : 1;
            int opsState = activeTasks > 0 ? 1 : 0; // 1 means "Busy/Working"

            return new
            {
                eng = engineState,
                idx = indexState,
                ops = opsState,
                scripts = scriptCount,
                active = activeTasks,
                total = totalJobs,
                today = jobsToday,
                sync = DateTime.Now.ToString("HH:mm:ss")
            };
        }
        catch (Exception)
        {
            return new { 
                eng = 2, 
                idx = 2, ops = 2, scripts = 0, active = 0, total = 0, today = 0 };
        }
    }

    public void Dispose() { }
}