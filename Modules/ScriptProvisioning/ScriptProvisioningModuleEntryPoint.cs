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
using ProcessorApplication.Utils;

using ScriptProvisioning.Configuration;
using ScriptProvisioning.Services;

namespace ScriptProvisioning;

/// <summary>
/// Script distribution between nodes. Publishes installed builds as packages over FileTransferModule and
/// installs packages from other nodes through ProcessingModule's installer. Has no install or transfer logic of its own.
/// </summary>
public class ScriptProvisioningModuleEntryPoint : IModule
{
    public const string MODULE_ID = "ScriptProvisioning";
    public string Name => "Script Exchange";
    public string ModuleId => MODULE_ID;
    public Version Version => new Version("1.0.0.0");

    public IEnumerable<string> ListAdvertisedCapabilities() => Array.Empty<string>();

    public ModuleDependency[] Dependencies => new[]
    {
        new ModuleDependency { ModuleId = "FileTransfer", MinVersion = new Version("1.0.0.0") },
        new ModuleDependency { ModuleId = "Processing", MinVersion = new Version("1.0.0.0") }
    };

    public IEnumerable<string> GetDefinedRoles() => Array.Empty<string>();
    public IEnumerable<string> GetRequiredRoles() => new[] { "Admin" };

    public IEnumerable<MenuItemViewModel> GetMenuItems(IServiceProvider services)
    {
        return new[]
        {
            new MenuItemViewModel
            {
                Name = "Script Exchange",
                IconClass = "fa-solid fa-arrows-rotate",
                Url = $"/{ModuleId}/Exchange/Index",
                Roles = "Admin"
            }
        };
    }

    public void ConfigureServices(IServiceCollection services, IConfiguration config)
    {
        services.AddModuleSettings<ScriptProvisioningSettings>(config, ModuleId);

        services.AddSingleton<IProvisioningStore, ProvisioningStore>();
        services.AddScoped<IScriptPublishingService, ScriptPublishingService>();
        // Singleton: install tasks run in the background and outlive the request that started them
        services.AddSingleton<IScriptAcquisitionService, ScriptAcquisitionService>();
    }

    public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
    {
        var moduleFolder = Path.GetDirectoryName(typeof(ScriptProvisioningModuleEntryPoint).Assembly.Location);
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
    }

    public IEnumerable<IConfigurationSource> GetConfigurationSources(IConfiguration initialConfig)
    {
        // Settings are saved in the main database
        yield break;
    }

    public void ConfigureEndpoints(IEndpointRouteBuilder endpoints)
    {
    }
}
