using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ProcessorApplication.Services;

public class SqliteConfigurationSource : IConfigurationSource
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _fallback;

    public SqliteConfigurationSource(IServiceScopeFactory scopeFactory, 
        IConfiguration appSettingsConfiguration)
    {
        _scopeFactory = scopeFactory;
        _fallback = appSettingsConfiguration;
    }

    public IConfigurationProvider Build(IConfigurationBuilder builder)
    {
        return new SqliteConfigurationProvider(_scopeFactory, _fallback);
    }
}