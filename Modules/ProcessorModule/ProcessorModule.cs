

using Common.Interfaces.Menu;
using Common.Models;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using ProcessorApplication.Configuration;
using ProcessorApplication.Database;
using ProcessorApplication.Infrastructure;

namespace ProcessorModule;
public class ProcessorModule : IModule
{
    public string Name => "Processor module";
    public string ModuleId => "Processor";

    public Version Version => new Version("0.0.0.1");

    public ModuleDependency[] Dependencies =>
        new ModuleDependency[] { 
            new ModuleDependency {
                ModuleId = "Main",
                MinVersion = new Version("1.0.0.0")
            } 
        };

    public List<MenuItemViewModel> GetMenuItems()
    {
        return new List<MenuItemViewModel>
        {
            new MenuItemViewModel { 
                Name = "Processing modules", 
                IconClass = "fa-solid fa-network-wired", 
                Url = "Processor/Process" },

            new MenuItemViewModel {
                Name = "Process status",
                IconClass = "fa-solid fa-network-wired",
                Url = "Processor/Process" },
            new MenuItemViewModel { Name = "Settings", IconClass = "fa-solid fa-shield-halved", Url = "/P2P/Security" }
        };
    }

    public void ConfigureServices(IServiceCollection services, IConfiguration config)
    {
        Console.WriteLine("PROCESSOR MODULE CONFIGURED");

        //var moduleConfig = config.GetSection("P2PNode");

        //// --- SQLite (per-module) ---
        //var dbPath = Path.Combine(AppContext.BaseDirectory, "Modules", "P2PNode", moduleConfig["DatabaseFile"] ?? "P2PNode.db");

        //services.AddDbContext<P2PNodeDbContext>(opt =>
        //    opt.UseSqlite($"Data Source={dbPath}"));

        //// --- MongoDB (demo: for another module, but safe) ---
        //var mongoCs = config.GetConnectionString("MongoDB") ?? config["MongoDB:ConnectionString"];
        //if (!string.IsNullOrWhiteSpace(mongoCs))
        //{
        //    services.AddSingleton<IMongoClient>(_ => new MongoClient(mongoCs));
        //    services.AddSingleton(provider =>
        //    {
        //        var client = provider.GetRequiredService<IMongoClient>();
        //        var dbName = config["MongoDB:DatabaseName"] ?? "OtherModuleData";
        //        return client.GetDatabase(dbName);
        //    });
        //}

        //// --- P2P Services ---
        //services.Configure<P2PSettings>(moduleConfig);
        //services.AddSingleton<P2PSettingsService>();
        //services.AddHostedService<P2PNodeService>();

        ////services.AddScoped<GossipAction>();
        ////services.AddScoped<AdvertiseAction>();
        //// ... all actions

        //// In P2PNodeModule.ConfigureServices()
        //var assembly = typeof(P2PNodeModule).Assembly;

        //// Register all IMessageHandler
        //var handlerTypes = assembly.GetTypes()
        //    .Where(t => typeof(IMessageHandler).IsAssignableFrom(t) && !t.IsAbstract);

        //foreach (var type in handlerTypes)
        //{
        //    services.AddScoped(type);
        //}

        //// Auto-register all INodeAction + IActionMarker
        //var actionTypes = typeof(P2PNodeModule).Assembly.GetTypes()
        //    .Where(t => typeof(INodeAction).IsAssignableFrom(t) &&
        //                t.GetInterfaces().Contains(typeof(IActionMarker)) &&
        //                !t.IsAbstract);

        //foreach (var type in actionTypes)
        //{
        //    services.AddScoped(type);
        //}

        //// Auto-initialize actions
        //services.AddHostedService(sp =>
        //    new ModuleActionInitializer(sp, assembly));


        //// Initialize on startup
        ////services.AddHostedService(sp =>
        ////    new ModuleActionInitializer(sp, typeof(P2PNodeModule).Assembly));


        //// --- MVC ---
        //services.AddControllersWithViews()
        //        .AddApplicationPart(typeof(P2PNodeModule).Assembly);
    }


    public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
    {
        //// --- Run migrations ---
        ////using (var scope = app.ApplicationServices.CreateScope())
        ////{
        ////    var db = scope.ServiceProvider.GetRequiredService<P2PNodeDbContext>();
        ////    db.Database.Migrate();
        ////}

        //// --- Static files ---
        //var wwwroot = Path.Combine(
        //    Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!,
        //    "wwwroot");

        //if (Directory.Exists(wwwroot))
        //{
        //    app.UseStaticFiles(new StaticFileOptions
        //    {
        //        FileProvider = new PhysicalFileProvider(wwwroot),
        //        RequestPath = "/p2p"
        //    });
        //}

        //app.UseEndpoints(endpoints =>
        //{
        //    // --- Routes ---
        //    endpoints.MapControllerRoute(
        //        name: "p2p",
        //        pattern: "p2p/{controller=Home}/{action=Index}/{id?}");
        //});
    }

    public void PrestartInit(IHost host)
    {
        //using (var scope = host.Services.CreateScope())
        //{
        //    var db = scope.ServiceProvider.GetRequiredService<P2PNodeDbContext>();
        //    db.Database.Migrate();
        //    //db.Database.ExecuteSqlRaw("PRAGMA journal_mode = WAL;");
        //    //db.Database.ExecuteSqlRaw("PRAGMA synchronous = NORMAL;");
        //    //db.Database.ExecuteSqlRaw("PRAGMA busy_timeout = 5000;");
        //}
    }


    public IEnumerable<IConfigurationSource> GetConfigurationSources(IConfiguration initialConfig)
    {
        var sqliteCs = initialConfig.GetConnectionString("SQLite");
        if (string.IsNullOrEmpty(sqliteCs))
            sqliteCs = initialConfig.GetValue<string>("Processor:ConnectionStrings:SQLite");

        if (string.IsNullOrWhiteSpace(sqliteCs))
        {
            yield break;
        }

        var dbContextOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(sqliteCs)
            .Options;

        yield return new DbConfigurationSource(dbContextOptions);
    }
}
