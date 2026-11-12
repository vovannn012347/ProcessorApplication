using Common.Models.Database;

using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

using ProcessorApplication.Database.Models;
using ProcessorApplication.Models;

namespace ProcessorApplication.Database;

public class AppDbContext : IdentityDbContext<ApplicationUser, IdentityRole, string>
{
    public DbSet<Setting> Settings { get; set; }
    // Server hash stamps are not in the database: encrypted files in the node home (HashStampFileStore).
    public DbSet<UserWidgetSetting> DashboardItemData { get; set; }

    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();

        base.ConfigureConventions(configurationBuilder);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {

        modelBuilder.Entity<Setting>()
            .HasKey(s => s.Id);

        modelBuilder.Entity<Setting>()
            .HasIndex(s => new { s.Key, s.Area })
            .IsUnique();


        modelBuilder.Entity<UserWidgetSetting>()
            .HasKey(s => new { s.UserId, s.WidgetId });
        modelBuilder.Entity<UserWidgetSetting>()
            .HasIndex(s => s.UserId);

        base.OnModelCreating(modelBuilder);
    }

    private class UtcDateTimeConverter : ValueConverter<DateTime, DateTime>
    {
        public UtcDateTimeConverter() : 
            base(v => v.Kind == DateTimeKind.Utc ? v : v.ToUniversalTime(),
                v => DateTime.SpecifyKind(v, DateTimeKind.Utc)) { }
    }
}

