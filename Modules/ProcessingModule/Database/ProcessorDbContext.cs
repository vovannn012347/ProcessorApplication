using Microsoft.EntityFrameworkCore;

using ProcessingModule.Database.Models;

namespace ProcessingModule.Database;

public class ProcessorDbContext : DbContext
{
    public DbSet<ScriptIndex> Scripts { get; set; }
    public DbSet<OrchestratedTask> Jobs { get; set; }
    public DbSet<OrchestratedTaskSubJob> ProcessingJobs { get; set; }

    public ProcessorDbContext(DbContextOptions<ProcessorDbContext> options) : base(options) { }
}