using Microsoft.EntityFrameworkCore;

using ProcessorApplication.Database;

namespace ProcessorApplication.Configuration;

public class DbConfigurationSource : IConfigurationSource
{
    private readonly DbContextOptions<AppDbContext> _dbContextOptions;

    public DbConfigurationSource(DbContextOptions<AppDbContext> dbContextOptions)
    {
        _dbContextOptions = dbContextOptions;
    }

    public IConfigurationProvider Build(IConfigurationBuilder builder)
    {
        return new DbConfigurationProvider(_dbContextOptions);
    }
}