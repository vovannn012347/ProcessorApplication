using Common.Interfaces.Menu;
using Common.Models;

namespace ProcessorApplication.Infrastructure;
public interface IModule
{
    string Name { get; }
    string ModuleId { get; }
    Version Version { get; }
    ModuleDependency[] Dependencies { get; }
    List<MenuItemViewModel> GetMenuItems(IServiceProvider services);
    IEnumerable<string> GetDefinedRoles();
    //config sources for ioptionsmonitor
    public IEnumerable<IConfigurationSource> GetConfigurationSources(IConfiguration initialConfig);
    void ConfigureServices(IServiceCollection services, IConfiguration config);
    void Configure(IApplicationBuilder app, IWebHostEnvironment env);
    void PrestartInit(IHost host);
}
    
