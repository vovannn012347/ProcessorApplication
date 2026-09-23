using System.Runtime.InteropServices;

using Common.Interfaces;
using Common.Interfaces.Menu;
using Common.Models;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

using ProcessorApplication.Infrastructure;
using ProcessorApplication.Infrastructure.Web;
using ProcessorApplication.Utils;

using ProviderlessModule.Configuration;
using ProviderlessModule.Configuration.Registry;
using ProviderlessModule.Configuration.Tunnel;
using ProviderlessModule.DashboardWidgets;
using ProviderlessModule.Infrastructure.Interfaces;
using ProviderlessModule.Services;
using ProviderlessModule.Services.Bootstrappers;
using ProviderlessModule.Services.Caretakers;
using ProviderlessModule.Services.Dashboard;
using ProviderlessModule.Services.EndpointProviders;
using ProviderlessModule.Services.Registry;
using ProviderlessModule.Services.Registry.Methods;
using ProviderlessModule.Services.Tunnel;
using ProviderlessModule.Services.Tunnel.Methods;

namespace ProviderlessModule;
public class ProviderlessModuleEntryPoint : IModule
{
    public const string MODULE_ID = "Providerless";
    public const string MODULE_NAMESPACE = "ProviderlessModule";
    public string Name => "Qr Access";
    public string ModuleId => MODULE_ID;

    public Version Version => new Version("1.0.0.0");

    public IEnumerable<string> ListAdvertisedCapabilities() => Array.Empty<string>();
    public ModuleDependency[] Dependencies => new[]
    {
        new ModuleDependency { 
            ModuleId = "Main", 
            MinVersion = new Version("1.0.0.0") 
        }
    };

    public IEnumerable<string> GetDefinedRoles() => Array.Empty<string>();

    public IEnumerable<string> GetRequiredRoles() => new[] { "Admin" };

    public IEnumerable<MenuItemViewModel> GetMenuItems(IServiceProvider services)
    {
        var menu = new List<MenuItemViewModel>();

        menu.Add(new MenuItemViewModel
        {
            Name = "Qr Access",
            IconClass = "fa-solid fa-qrcode",
            Url = "/Providerless/Home/Index",
            Roles = "Admin"
        });

        menu.Add(new MenuItemViewModel
        {
            Name = "Settings",
            IconClass = "fa-solid  fa-cog",
            Url = "/Providerless/Settings/Index",
            Roles = "Admin"
        });

        return menu;
    }

    public void ConfigureServices(IServiceCollection services, IConfiguration config)
    {
        //services.Configure<RazorViewEngineOptions>(options =>
        //{
        //    options.ViewLocationExpanders.Add(new ProviderlessViewLocationExpander());
        //});

        services.TryAddHttpClient();

        // Binds the "ProcessorDirectories" property from the current config section
        services.AddModuleSettings<PortalAccessSettings>(config, ModuleId);
        services.AddSingleton<ILocalDataProvider, LocalDataProvider>();
        services.AddSingleton<IPortalControlSignal, PortalControlSignal>();

        // Runtime portal state
        services.AddSingleton<PortalState>();

        //services.AddModuleSettings<NgrokSettings>(config, ModuleId);
        services.AddModuleSettings<CloudflareSettings>(config, ModuleId);

        services.AddSingleton<ITunnelProvider, StaticProvider>();

        services.AddSingleton<ITunnelProvider, CloudflareTunnelProvider>();
        //services.AddHttpClient<ITunnelProvider, CloudflareTunnelProvider>();

        //services.AddSingleton<ITunnelProvider, NgrokTunnelProvider>();

        services.AddSingleton<ITunnelSelector, TunnelSelector>();

        // This service handles downloading/checking for .exe or linux binaries
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            services.AddSingleton<IBinaryBootstrapper, WindowsBinaryBootstrapper>();
            services.AddSingleton<IProcessCaretaker, WindowsProcessCaretaker>();
        }
        else
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            services.AddSingleton<IBinaryBootstrapper, LinuxBinaryBootstrapper>();
            services.AddSingleton<IProcessCaretaker, LinuxProcessCaretaker>();
        }

        services.AddModuleSettings<NoneRegistrySettings>(config, ModuleId);
        services.AddModuleSettings<GithubSettings>(config, ModuleId);
        //services.AddModuleSettings<GoogleDocsSettings>(config, ModuleId);
        //services.AddModuleSettings<RestSettings>(config, ModuleId);

        services.AddSingleton<IUrlRegistry, NoneRegistry>();
        services.AddSingleton<IUrlRegistry, GitHubRegistry>(); // Communicates with GitHub API
        //services.AddHttpClient<IUrlRegistry, GoogleDocsHubRegistry>();
        //services.AddHttpClient<IUrlRegistry, RestHubRegistry>();

        //services.AddSingleton<IUrlRegistry, GoogleDocsRegistry>(); // Communicates with Google Docs API
        //services.AddHttpClient<IUrlRegistry, GoogleDocsRegistry>();

        //services.AddSingleton<IUrlRegistry, RestRegistry>(); // Communicates with Rest API
        //services.AddHttpClient<IUrlRegistry, RestRegistry>();

        services.AddSingleton<IRegistrySelector, RegistrySelector>();

        // This is the background worker that runs the whole process
        services.AddHostedService<PortalOrchestratorService>();

        services.AddScoped<IWidgetProvider, AccessWidgetProvider>();
        services.AddSingleton<AccessStatusTracker>();
        services.AddHostedService<AccessReachabilityService>();

        services.AddScoped<IEndpointProvider, ProviderlessTunnelEndpointProvider>();
    }


    public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
    {
        var assembly = typeof(ProviderlessModuleEntryPoint).Assembly;
        var moduleFolder = Path.GetDirectoryName(assembly.Location);
        var wwwrootPath = Path.Combine(moduleFolder!, "wwwroot");

        if (Directory.Exists(wwwrootPath))
        {
            app.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = new PhysicalFileProvider(wwwrootPath),
                RequestPath = $"/{ModuleId}"
            });
        }
    }

    public void PrestartInit(IHost host)
    {
        //using var scope = host.Services.CreateScope();
        //var services = scope.ServiceProvider;


    }

    public IEnumerable<IConfigurationSource> GetConfigurationSources(IConfiguration initialConfig)
    {
        //settigns are saved in main database
        //this is here in case module has its own settings sources and mechanisms
        yield break;
    }

    public void ConfigureEndpoints(IEndpointRouteBuilder endpoints)
    {

    }
}
