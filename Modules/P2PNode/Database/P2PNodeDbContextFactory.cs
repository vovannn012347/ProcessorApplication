using System;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

using ProcessorApplication.Sqlite;

namespace ProcessorApplication.Database;

public class P2PNodeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<AppDbContext>();
        optionsBuilder.UseSqlite("Data Source=medicalsystem.db");

        return new AppDbContext(optionsBuilder.Options);
    }
}