
using Common.Interfaces.Menu;

namespace ProcessorApplication.Infrastructure;
public interface IModule
{
    string Name { get; }
    string ModuleId { get; }
    List<MenuItemViewModel> GetMenuItems();
    void ConfigureServices(IServiceCollection services, IConfiguration config);
    void Configure(IApplicationBuilder app, IWebHostEnvironment env);
    void PrestartInit(IHost host);
}
