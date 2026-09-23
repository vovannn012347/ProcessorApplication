using System.ComponentModel.DataAnnotations;

using Common.Interfaces;
using Common.Models.Database;

using Microsoft.EntityFrameworkCore;

using ProcessorApplication.Database;
using ProcessorApplication.Infrastructure;

namespace ProcessorApplication.Configuration;

public static class ConfigurationBootstrapper
{
    public static async Task SeedAsync(WebApplication app, IEnumerable<IModule> modules)
    {
        using var scope = app.Services.CreateScope();

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingService>();
        var config = app.Configuration;

        // Existing keys in DB (Area:Key)
        var existing = await db.Settings
            .AsNoTracking()
            .ToDictionaryAsync(
                s => $"{s.Area}:{s.Key}",
                s => s.Value,
                StringComparer.OrdinalIgnoreCase);

        bool inserted = false;

        var moduleIds = modules
            .Select(m => m.ModuleId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        settings.SetAutoUpdate(false);

        foreach (var kv in config.AsEnumerable())
        {
            if (string.IsNullOrEmpty(kv.Value))
                continue;

            var idx = kv.Key.IndexOf(':');
            if (idx < 0)
                continue;

            var area = kv.Key[..idx];

            // Only persist settings owned by modules.
            if (!moduleIds.Contains(area))
                continue;

            // Already present? Leave DB authoritative.
            if (existing.ContainsKey(kv.Key))
                continue;

            db.Settings.Add(new Setting
            {
                Area = area,
                Key = kv.Key[(idx + 1)..],
                Value = kv.Value
            });

            inserted = true;
        }

        if (inserted)
        {
            await db.SaveChangesAsync();

            settings.SetAutoUpdate(true);
            settings.ForceUpdateOptionsMonitor();
        }
        else
        {
            settings.SetAutoUpdate(true);
        }
    }
}