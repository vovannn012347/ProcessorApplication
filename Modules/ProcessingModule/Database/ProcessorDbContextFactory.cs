using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

using ProcessingModule.Database.Models;

namespace ProcessingModule.Database;

public class ProcessorDbContextFactory : IDesignTimeDbContextFactory<ProcessorDbContext>
{
    public ProcessorDbContext CreateDbContext(string[] args)
    {
        var basePath = Directory.GetCurrentDirectory();

        var configuration = new ConfigurationBuilder()
            .SetBasePath(basePath)
            .AddJsonFile("appsettings.Processor.json", optional: true)
            .Build();

        var connectionString = configuration.GetConnectionString("SQLite");

        if (string.IsNullOrEmpty(connectionString))
        {
            connectionString = "Data Source=processordata.db";
        }

        var optionsBuilder = new DbContextOptionsBuilder<ProcessorDbContext>();
        optionsBuilder.UseSqlite(connectionString);

        return new ProcessorDbContext(optionsBuilder.Options);
    }
}