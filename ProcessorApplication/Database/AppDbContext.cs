using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

using ProcessorApplication.Sqlite.Models;

namespace ProcessorApplication.Sqlite;

public class AppDbContext : IdentityDbContext<IdentityUser, IdentityRole, string>
{
    //public DbSet<Peer> Peers { get; set; }
    //public DbSet<Tracker> Trackers { get; set; }
    public DbSet<Setting> Settings { get; set; }

    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        //modelBuilder.Entity<Peer>()
        //.HasIndex(p => p.HashKey)
        //.IsUnique();

        //modelBuilder.Entity<Tracker>()
        //.HasIndex(t => t.HashKey)
        //.IsUnique();


        modelBuilder.Entity<Setting>()
            .HasIndex(s => new { s.Key, s.Area })
            .IsUnique();


        base.OnModelCreating(modelBuilder);
    }
}

