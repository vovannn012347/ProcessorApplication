using Microsoft.EntityFrameworkCore;

using ProcessingModule.Database.Models;

namespace ProcessingModule.Database;

public class ProcessorDbContext : DbContext
{
    public DbSet<ScriptIndex> Scripts { get; set; }
    public DbSet<OrchestratedTask> Jobs { get; set; }
    public DbSet<OrchestratedTaskSubJob> ProcessingJobs { get; set; }
    public DbSet<ResultChainEntry> ResultChain { get; set; }

    public ProcessorDbContext(DbContextOptions<ProcessorDbContext> options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ResultChainEntry>()
            .HasIndex(e => new { e.OwnerId, e.Sequence })
            .IsUnique();

        modelBuilder.Entity<ResultChainEntry>()
            .HasIndex(e => e.StepId);

        base.OnModelCreating(modelBuilder);
    }
}