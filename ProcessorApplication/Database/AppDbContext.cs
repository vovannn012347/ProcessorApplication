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
    public DbSet<ServerHashStamp> HashStamps { get; set; }

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

        modelBuilder.Entity<ServerHashStamp>()
            .HasKey(s => s.Id);

        modelBuilder.Entity<ServerHashStamp>()
            .HasIndex(s => s.StampTime)
            .IsUnique();

        modelBuilder.Entity<Setting>()
            .HasKey(s => s.Id);

        modelBuilder.Entity<Setting>()
            .HasIndex(s => new { s.Key, s.Area })
            .IsUnique();


        base.OnModelCreating(modelBuilder);
    }

    private class UtcDateTimeConverter : ValueConverter<DateTime, DateTime>
    {
        public UtcDateTimeConverter() : 
            base(v => v.Kind == DateTimeKind.Utc ? v : v.ToUniversalTime(),
                v => DateTime.SpecifyKind(v, DateTimeKind.Utc)) { }
    }
}

