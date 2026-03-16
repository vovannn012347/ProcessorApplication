using System.ComponentModel.DataAnnotations;

using Microsoft.EntityFrameworkCore;

using ProcessorApplication.Database;

namespace ProcessorApplication.Configuration;

public class DbConfigurationProvider : ConfigurationProvider, IDbConfigurationProvider
{
    private readonly DbContextOptions<AppDbContext> _dbContextOptions;

    public DbConfigurationProvider(DbContextOptions<AppDbContext> dbContextOptions)
    {
        _dbContextOptions = dbContextOptions;
    }

    public override void Load()
    {
        // InternalLoad handles the actual dictionary building
        LoadInternal();
    }

    public void LoadInternal()
    {
        try
        {
            // Create a dedicated DbContext instance for configuration loading.
            using var context = new AppDbContext(_dbContextOptions);

            var dbSettings = context.Settings
                .AsNoTracking()
                .Where(s => s.Value != null && !string.IsNullOrWhiteSpace(s.Area))
                .ToList();

            Data = dbSettings
                .ToDictionary(
                    c => $"{c.Area}:{c.Key}",
                    c => c.Value!,
                    StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error loading configuration from DB: {ex.Message}");
            Data = new Dictionary<string, string>();
        }
    }

    /// <summary>
    /// CRITICAL: Forces a reload of the configuration system, updating IOptionsMonitor<T>.
    /// </summary>
    public void TriggerReload()
    {
        this.LoadInternal();
        this.OnReload();
    }
}
