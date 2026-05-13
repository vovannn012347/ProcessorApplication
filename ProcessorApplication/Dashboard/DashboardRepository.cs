using System.Reflection;
using System.Runtime.Loader;

using Common;
using Common.Models;

using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

using ProcessorApplication.Database;
using ProcessorApplication.Database.Models;

namespace ProcessorApplication.Dashboard;

// Active session tracking
public interface IDashboardRepository
{
    Task<List<UserWidgetSetting>> GetWidgetSettingsAsync(string userId);
    Task UpdateWidgetSettingAsync(UserWidgetSetting setting);
}

public class DashboardRepository : IDashboardRepository
{
    private readonly AppDbContext _db;

    public DashboardRepository(AppDbContext db)
    {
        _db = db;
    }

    public async Task<List<UserWidgetSetting>> GetWidgetSettingsAsync(string userId)
    {
        return await _db.DashboardItemData
            .Where(s => s.UserId == userId)
            .AsNoTracking()
            .ToListAsync();
    }

    public async Task UpdateWidgetSettingAsync(UserWidgetSetting setting)
    {
        var existing = await _db.DashboardItemData
            .FirstOrDefaultAsync(s => s.UserId == setting.UserId && s.WidgetId == setting.WidgetId);

        if (existing != null)
        {
            existing.GeneralSettingsJson = setting.GeneralSettingsJson;
            existing.SmallScreenSettingsJson = setting.SmallScreenSettingsJson;
            existing.LargeScreenSettingsJson = setting.LargeScreenSettingsJson;
            _db.DashboardItemData.Update(existing);
        }
        else
        {
            await _db.DashboardItemData.AddAsync(setting);
        }

        await _db.SaveChangesAsync();
    }
}