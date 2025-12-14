
using Common.Interfaces.Menu;
using Common.Models;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using PortabilityModule.Models;
using PortabilityModule.Services;

using ProcessorApplication.Configuration;
using ProcessorApplication.Database;
using ProcessorApplication.Infrastructure;
using ProcessorApplication.Utils;

namespace PortabilityModule;

public class DataPortabilityModule : IModule
{
    public string Name => "Data Portability";
    public string ModuleId => "dataportability";

    public Version Version => new Version("0.0.0.1");

    public ModuleDependency[] Dependencies =>
        new ModuleDependency[] {
            new ModuleDependency {
                ModuleId = "Main",
                MinVersion = new Version("1.0.0.0")
            }
        };
    List<MenuItemViewModel> GetMenuItems() => new List<MenuItemViewModel>
        {
            new MenuItemViewModel { Name = "Data Export/Import", IconClass = "fa-solid fa-file-export", Url = "/DataExport/Index" }
        };

    List<MenuItemViewModel> IModule.GetMenuItems()
    {
        return GetMenuItems();
    }

    public IEnumerable<string> GetDefinedRoles()
    {
        throw new NotImplementedException();
    }

    public IEnumerable<IConfigurationSource> GetConfigurationSources(IConfiguration initialConfig)
    {
        yield break;
        
        var sqliteCs = initialConfig.GetConnectionString("SQLite");
        if (string.IsNullOrEmpty(sqliteCs))
            sqliteCs = initialConfig.GetValue<string>("dataportability:ConnectionStrings:SQLite");

        if (string.IsNullOrWhiteSpace(sqliteCs))
        {
            yield break;
        }

        var dbContextOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(sqliteCs)
            .Options;

        yield return new DbConfigurationSource(dbContextOptions);
    }

    public void ConfigureServices(IServiceCollection services, IConfiguration config)
    {
        services.AddModuleSettings<DataPortabilitySettings>(config, ModuleId);
        services.AddScoped<IDataPortabilityService, DataPortabilityService>();
    }

    public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
    {

    }

    public void PrestartInit(IHost host)
    {

    }
}
