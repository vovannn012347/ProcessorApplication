using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Common;

public interface IModuleStartup
{
    // Add module-specific configuration sources
    void ConfigureAppConfiguration(HostBuilderContext context, IConfigurationBuilder builder);

    // Register services (DbContext, singletons, etc.)
    void ConfigureServices(HostBuilderContext context, IServiceCollection services);

    // Configure middleware/endpoints/migrations
    void ConfigureApp(IApplicationBuilder app);
}
