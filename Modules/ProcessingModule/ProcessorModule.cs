using System.Runtime.InteropServices;

using Common.Interfaces;
using Common.Interfaces.Menu;
using Common.Models;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Razor;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

using ProcessorApplication.Infrastructure;
using ProcessorApplication.Utils;

using ProcessingModule.Configuration;
using ProcessingModule.DashboardWidgets;
using ProcessingModule.Database;
using ProcessingModule.Infrastructure;
using ProcessingModule.Infrastructure.Web;
using ProcessingModule.Services;
using ProcessingModule.Services.Processing;
using ProcessingModule.Services.Sandboxing;
using ProcessingModule.Services.Sandboxing.Windows;

namespace ProcessingModule;
public class ProcessorModule : IModule
{
    public const string MODULE_ID = "Processing";
    public string Name => "Processor Module";
    public string ModuleId => MODULE_ID;

    public const string RoleProcessRunner = "ProcessRunner";

    public Version Version => new Version("1.0.0.0");

    public ModuleDependency[] Dependencies => new[]
    {
        new ModuleDependency { 
            ModuleId = "Main", 
            MinVersion = new Version("1.0.0.0") 
        }
    };

    public IEnumerable<string> GetDefinedRoles() 
    { 
        return new[] { RoleProcessRunner };
    }

    public IEnumerable<string> GetRequiredRoles() => new[] { "User" };

    public List<MenuItemViewModel> GetMenuItems(IServiceProvider services)
    {
        var menu = new List<MenuItemViewModel>();


        // Personal History (Personal Queue) - Available to Runners and Admins
        menu.Add(new MenuItemViewModel
        {
            Name = "Processing History",
            IconClass = "fa-solid fa-clock",
            Url = $"/{MODULE_ID}/Home/Queue"
        });

        //Admin View(Global Queue) -Strict Admin only
        menu.Add(new MenuItemViewModel
        {
            Name = "Processing History (Admin)",
            IconClass = "fa-solid fa-server",
            Url = $"/{MODULE_ID}/Admin/QueueAdmin",
            Roles = "Admin"
        });

        // Run Scripts (Main Action) - Available to Runners and Admins
        // script reindexins is also done here
        menu.Add(new MenuItemViewModel
        {
            Name = "Run Scripts",
            IconClass = "fa-solid fa-play",
            Url = $"/{MODULE_ID}/Home/ScriptList",
            Roles = $"{RoleProcessRunner},Admin"
        });


        // Module Settings - Strict Admin only
        menu.Add(new MenuItemViewModel
        {
            Name = "Processor Settings",
            IconClass = "fa-solid fa-cog",
            Url = $"/{MODULE_ID}/Settings/Index",
            Roles = "Admin"
        });

        return menu;
    }

    public void ConfigureServices(IServiceCollection services, IConfiguration config)
    {
        services.Configure<RazorViewEngineOptions>(options =>
        {
            options.ViewLocationExpanders.Add(new ProcessorViewLocationExpander());
        });

        var rawConnectionString = config.GetConnectionString("SQLite");

        if (!string.IsNullOrWhiteSpace(rawConnectionString))
        {
            var moduleFolder = Path.GetDirectoryName(typeof(ProcessorModule).Assembly.Location);
            var builder = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(rawConnectionString);
            if (!Path.IsPathRooted(builder.DataSource))
            {
                builder.DataSource = Path.Combine(moduleFolder!, builder.DataSource);
            }
            var absoluteConnectionString = builder.ToString();

            services.AddDbContext<ProcessorDbContext>(options =>
                options.UseSqlite(absoluteConnectionString),
                contextLifetime: ServiceLifetime.Scoped,
                optionsLifetime: ServiceLifetime.Singleton);

            //for singletons
            services.AddDbContextFactory<ProcessorDbContext>(options =>
                options.UseSqlite(absoluteConnectionString));
        }


        // Binds the "ProcessorDirectories" property from the current config section
        services.AddModuleSettings<ProcessorSettings>(config, ModuleId);
        services.AddModuleSettings<PythonProcessingSettings>(config, ModuleId);

        services.AddModuleSettings<NoneSandboxSettings>(config, ModuleId);
        services.AddModuleSettings<OsSandboxSettings>(config, ModuleId);
        services.AddModuleSettings<DockerSandboxSettings>(config, ModuleId);

        services.AddScoped<IScriptIndexer, ScriptIndexer>();

        services.AddScoped<IProcessingService, ProcessingService>();

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            services.AddSingleton<ISandboxProcessing, WindowsNoneSandboxPython>();
            services.AddSingleton<ISandboxProcessing, WindowsOsSandboxPython>();
            services.AddSingleton<ISandboxProcessing, WindowsDockerSandboxPython>();
            services.AddScoped<IOsUserManagementService, WindowsOsUserManagementService>();
        }
        //else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        //{
        //    services.AddScoped<ISandboxProcessing, LinuxNoneSandbox>();
        //    services.AddScoped<ISandboxProcessing, LinuxOsSandbox>();
        //    services.AddScoped<ISandboxProcessing, LinuxDockerSandbox>();
        //}

        //services.AddHostedService<OsUserProvisioningTask>();

        services.AddSingleton<ISandboxProvider, SandboxProvider>();

        services.AddSingleton<ProcessingQueue>();
        services.AddSingleton<TaskControlMonitor>();
        services.AddHostedService<JobBackgroundService>();

        services.AddScoped<IWidgetProvider, ProcessWidgetProvider>();
    }


    public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
    {
        var assembly = typeof(ProcessorModule).Assembly;
        var moduleFolder = Path.GetDirectoryName(assembly.Location);
        var wwwrootPath = Path.Combine(moduleFolder!, "wwwroot");

        if (Directory.Exists(wwwrootPath))
        {
            app.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = new PhysicalFileProvider(wwwrootPath),
                // This allows <link href="/Processing/css/processor.min.css" />
                // It MUST match your ModuleId
                RequestPath = "/Processing" //$"/{ModuleId}"
            });
        }
    }

    public void PrestartInit(IHost host)
    {
        using var scope = host.Services.CreateScope();
        var services = scope.ServiceProvider;

        try
        {
            var db = services.GetRequiredService<ProcessorDbContext>();

            var connectionString = db.Database.GetConnectionString();
            var builder = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(connectionString);
            var dbFilePath = builder.DataSource;
            var directory = Path.GetDirectoryName(dbFilePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            db.Database.Migrate();

            // Optimal SQLite performance settings for local indexing
            db.Database.ExecuteSqlRaw("PRAGMA journal_mode = WAL;");
            db.Database.ExecuteSqlRaw("PRAGMA synchronous = NORMAL;");
            db.Database.ExecuteSqlRaw("PRAGMA busy_timeout = 5000;");
        }
        catch (Exception ex)
        {
            // Log or handle the error if the DB fails to initialize
            Console.WriteLine($"[ProcessorModule] DB Migration Failed: {ex.Message}");
        }
    }

    public IEnumerable<IConfigurationSource> GetConfigurationSources(IConfiguration initialConfig)
    {
        //settigns are saved in main database
        yield break;
    }
}
