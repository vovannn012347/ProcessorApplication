using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Common.Models.Database;

using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace ProcessorModule.Sqlite;

public class ProcessingDbContext : DbContext
{
    public DbSet<Setting> Settings { get; set; }
    public DbSet<ProcessorScript> ProcessScripts { get; set; }
    public DbSet<ProcessRecord> ProcessRecords { get; set; }

    public ProcessingDbContext(DbContextOptions<ProcessingDbContext> options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Setting>()
            .HasIndex(s => new { s.Key, s.Area })
            .IsUnique();

        base.OnModelCreating(modelBuilder);
    }
}

