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

        // Lists are owned by the DB as a whole once any part of them is stored there. Without this, a list
        // shortened in the UI got the missing indexes re-added from appsettings on the next start
        // (UI [a] + file [x, y] -> [a, y]), and a list emptied in the UI came back completely.
        var dbArrayPaths = CollectArrayPaths(existing.Keys);

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

            // Part of a list the DB already holds (or holds as an empty list)? Leave the whole list alone.
            if (GetArrayPaths(kv.Key).Any(dbArrayPaths.Contains))
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

    /// <summary>
    /// Every list path a key sits in: "P2P:P2PSettings:Endpoints:0" -> "P2P:P2PSettings:Endpoints";
    /// "A:Rules:2:Domains:0" -> "A:Rules" and "A:Rules:2:Domains".
    /// </summary>
    internal static IEnumerable<string> GetArrayPaths(string key)
    {
        var parts = key.Split(':');
        for (int i = 1; i < parts.Length; i++)
        {
            if (parts[i].Length > 0 && parts[i].All(char.IsDigit))
                yield return string.Join(':', parts, 0, i);
        }
    }

    /// <summary>
    /// List paths the DB already owns: every list a stored key sits in, plus every stored key itself, because
    /// SettingService stores a value-less marker row at the list's own path when it saves a list (that is how an
    /// emptied list is remembered).
    /// </summary>
    private static HashSet<string> CollectArrayPaths(IEnumerable<string> dbKeys)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in dbKeys)
        {
            paths.Add(key);
            foreach (var path in GetArrayPaths(key))
                paths.Add(path);
        }
        return paths;
    }
}