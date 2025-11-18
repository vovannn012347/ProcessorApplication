using Common.Interfaces.Menu;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using ProcessorApplication.Infrastructure;

namespace ProcessorApplication;
public class MainModule : IModule
{
    public string Name => "Main module";
    public string ModuleId => "main";

    public List<MenuItemViewModel> GetMenuItems()
    {
        return new List<MenuItemViewModel>
        {
            new MenuItemViewModel { Name = "Dashboard", IconClass = "fa-solid fa-network-wired", Url = "/Home/Index" },
            new MenuItemViewModel { Name = "Settings", IconClass = "fa-solid fa-users", Url = "/Settings/Index" },
            new MenuItemViewModel { Name = "Security", IconClass = "fa-solid fa-shield-halved", Url = "/Settings/Security" }
        };
    }

    public void ConfigureServices(IServiceCollection services, IConfiguration config)
    {
    }


    public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
    {
      
    }

    public void PrestartInit(IHost host)
    {
       
    }
}
