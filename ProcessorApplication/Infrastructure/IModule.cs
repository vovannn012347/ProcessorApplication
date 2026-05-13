using Common.Interfaces.Menu;
using Common.Models;

namespace ProcessorApplication.Infrastructure;
public interface IModule
{
    /// <summary>
    /// Identifier for module, human-readable
    /// </summary>
    string Name { get; }
    /// <summary>
    /// modeule identifier
    /// </summary>
    string ModuleId { get; }
    /// <summary>
    /// current version
    /// </summary>
    Version Version { get; }
    /// <summary>
    /// Dependencies on other modules, include modeule ids, minimum and maximum version
    /// </summary>
    ModuleDependency[] Dependencies { get; }
    /// <summary>
    /// Default left-side menu items
    /// </summary>
    /// <param name="services"></param>
    /// <returns></returns>
    List<MenuItemViewModel> GetMenuItems(IServiceProvider services);
    /// <summary>
    /// Roles that module defines
    /// </summary>
    /// <returns></returns>
    IEnumerable<string> GetDefinedRoles();
    /// <summary>
    /// Roles that module minimally requires
    /// </summary>
    /// <returns></returns>
    IEnumerable<string> GetRequiredRoles();
    /// <summary>
    /// module configuration sources, here module defines connection to its own configuration source - like local database
    /// </summary>
    /// <param name="initialConfig">initial config loaded from respective module appsettings file</param>
    /// <returns></returns>
    public IEnumerable<IConfigurationSource> GetConfigurationSources(IConfiguration initialConfig);
    /// <summary>
    /// Register services - per scope, transent, singletons, 
    /// </summary>
    /// <param name="services">services collection</param>
    /// <param name="config">config loaded from respective module appsettings file </param>
    void ConfigureServices(IServiceCollection services, IConfiguration config);
    /// <summary>
    /// Let modules configure middleware, routes, static files for themselves
    /// </summary>
    /// <param name="app">application provided</param>
    /// <param name="env">web host enviroment provided</param>
    void Configure(IApplicationBuilder app, IWebHostEnvironment env);
    /// <summary>
    /// apply prestart initialization actions - like database migrations, etc starting actions
    /// </summary>
    /// <param name="host">source for services collection</param>
    void PrestartInit(IHost host);
}
    
