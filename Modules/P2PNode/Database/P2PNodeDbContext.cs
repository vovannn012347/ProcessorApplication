using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ProcessorApplication.Sqlite.Models;

using Microsoft.EntityFrameworkCore;

namespace ProcessorApplication.Database;

public class P2PNodeDbContext : DbContext
{
    //public DbSet<Peer> Peers { get; set; }
    //public DbSet<Tracker> Trackers { get; set; }
    public DbSet<Setting> Settings { get; set; }

    public P2PNodeDbContext(DbContextOptions<P2PNodeDbContext> options) : base(options)
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
        .HasIndex(s => s.Key)
        .IsUnique(); 
        
        base.OnModelCreating(modelBuilder);
    }
}

