using System.Reflection;

using Common.Interfaces;

using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

using ProcessorApplication.Sqlite;

using ProcessorApplication.Infrastructure;
using ProcessorApplication.Services;
using ProcessorApplication;

const string ModuleDir = "Modules";

var builder = WebApplication.CreateBuilder(args);

// config
builder.Configuration
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
    .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: true, reloadOnChange: true);

var modulesRoot = Path.Combine(AppContext.BaseDirectory, ModuleDir);

if (!Directory.Exists(modulesRoot)) Directory.CreateDirectory(modulesRoot);

if (Directory.Exists(modulesRoot))
{
    foreach (var dir in Directory.GetDirectories(modulesRoot))
    {
        var moduleName = Path.GetFileName(dir);
        var file = Path.Combine(dir, $"appsettings.{moduleName}.json");
        if (File.Exists(file))
            builder.Configuration.AddJsonFile(file, optional: true, reloadOnChange: true);
    }
}

// core services
builder.Services.AddControllersWithViews()
    .AddRazorRuntimeCompilation(options =>
    {
        // Add the Modules directory to the list of places Razor looks for files
        var modulesPath = Path.Combine(AppContext.BaseDirectory, "Modules");
        if (Directory.Exists(modulesPath))
        {
            options.FileProviders.Add(
                new Microsoft.Extensions.FileProviders.PhysicalFileProvider(modulesPath)
            );
        }
    });

// database
var sqliteCs = builder.Configuration.GetConnectionString("SQLite");
if (!string.IsNullOrWhiteSpace(sqliteCs))
{
    builder.Services.AddDbContext<AppDbContext>(opt =>
        opt.UseSqlite(sqliteCs, sqliteOptions => sqliteOptions.CommandTimeout(30)));
}

// identity
builder.Services.AddIdentity<IdentityUser, IdentityRole>()
    .AddEntityFrameworkStores<AppDbContext>()
    .AddDefaultTokenProviders();

// shared settings
builder.Services.AddScoped<ISettingService, SettingService>();
builder.Services.AddHostedService<SettingsInitializer>(); 
builder.Services.AddSingleton<IModuleService, ModuleService>();

// module discovery and registration
var modules = ModuleLoader.DiscoverModules(AppContext.BaseDirectory);
foreach (var m in modules)
{
    builder.Services.AddSingleton(m);
    m.ConfigureServices(builder.Services, builder.Configuration);
}
builder.Services.AddSingleton<IModule, MainModule>();

// build
var app = builder.Build();

// prestart init actions
PrestartInit(app);
var moduleInstances = app.Services.GetServices<IModule>();
foreach (var m in moduleInstances)
    m.PrestartInit(app);

// hot reload
if (app.Environment.IsDevelopment())
{
    var watcher = new FileSystemWatcher(modulesRoot, "*.dll")
    {
        IncludeSubdirectories = true,
        EnableRaisingEvents = true,
        NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite
    };
    watcher.Changed += (sender, e) =>
    {
        app.Logger.LogWarning("Module DLL changed: {File}. Triggering restart...", e.FullPath);
        app.Lifetime.StopApplication();
    };
}

// request pipeline
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();  // Required for Identity
app.UseAuthorization();

// Let modules configure middleware, routes, static files
foreach (var m in moduleInstances)
    m.Configure(app, app.Environment);


// endpoints
app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");


// testing
//app.MapGet("/diag/modules", () =>
//    string.Join(", ", moduleInstances.Select(m => m.GetType().Assembly.GetName().Name)));


await app.RunAsync();

void PrestartInit(IHost host)
{
    using var scope = host.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

    db.Database.Migrate();

    db.Database.ExecuteSqlRaw("PRAGMA journal_mode = WAL;");
    db.Database.ExecuteSqlRaw("PRAGMA synchronous = NORMAL;");
    db.Database.ExecuteSqlRaw("PRAGMA busy_timeout = 5000;");
}

//this does some bullshit
//var app = Host.CreateDefaultBuilder(args);
//var host = 
//    Host.CreateDefaultBuilder(args)
//    // configuration
//    .ConfigureAppConfiguration((ctx, cfg) =>
//    {
//        cfg.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
//           .AddJsonFile($"appsettings.{ctx.HostingEnvironment.EnvironmentName}.json",
//                        optional: true, reloadOnChange: true);

//        // ---- auto-load every module's appsettings.*.json ----
//        var modulesRoot = Path.Combine(AppContext.BaseDirectory, ModuleDir);
//        if (Directory.Exists(modulesRoot))
//        {
//            foreach (var dir in Directory.GetDirectories(modulesRoot))
//            {
//                var file = Path.Combine(dir, $"appsettings.{Path.GetFileName(dir)}.json");
//                if (File.Exists(file))
//                    cfg.AddJsonFile(file, optional: true, reloadOnChange: true);
//            }
//        }
//    })
//    // core services
//    .ConfigureServices((ctx, services) =>
//    {
//        // ----- MVC / Razor -----
//        services.AddControllersWithViews();

//        // Global SQLite DB (for settings, auth, etc.)
//        var sqliteCs = ctx.Configuration.GetConnectionString("SQLite");
//        if (!string.IsNullOrWhiteSpace(sqliteCs))
//        {
//            services.AddDbContext<AppDbContext>(opt => opt.UseSqlite(sqliteCs,
//                sqliteOptions => sqliteOptions.CommandTimeout(30)));
//        }

//        services.AddIdentity<IdentityUser, IdentityRole>()
//            .AddEntityFrameworkStores<AppDbContext>()
//            .AddDefaultTokenProviders();

//        // Shared settings system (DB-first → JSON fallback)
//        services.AddScoped<ISettingService, SettingService>();
//        services.AddHostedService<SettingsInitializer>();

//    })
//    // module discovery
//    .ConfigureServices((ctx, services) =>
//    {
//        var modules = ModuleLoader.DiscoverModules(AppContext.BaseDirectory);
//        foreach (var m in modules)
//        {
//            // Register the module *instance* so its Configure() can resolve services
//            services.AddSingleton(m);
//            m.ConfigureServices(services, ctx.Configuration);
//        }
//    })
//    // web host pipeline
//    .ConfigureWebHostDefaults(webBuilder =>
//    {
//        webBuilder.Configure(app =>
//        {
//            var env = app.ApplicationServices.GetRequiredService<IWebHostEnvironment>();
//            var lifetime = app.ApplicationServices.GetRequiredService<IHostApplicationLifetime>();

//            // ---- production safety ----
//            if (env.IsDevelopment())
//            {
//                app.UseDeveloperExceptionPage();

//                var modulesRoot = Path.Combine(env.ContentRootPath, ModuleDir);
//                if (Directory.Exists(modulesRoot))
//                {
//                    var watcher = new FileSystemWatcher(modulesRoot, "*.dll")
//                    {
//                        IncludeSubdirectories = true,
//                        EnableRaisingEvents = true,
//                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite
//                    };

//                    watcher.Changed += (sender, e) =>
//                    {
//                        // Graceful shutdown → dotnet watch / VS will restart
//                        app.ApplicationServices
//                           .GetService<ILogger<Program>>()
//                           ?.LogWarning("Module DLL changed: {File}. Triggering restart...", e.FullPath);

//                        lifetime.StopApplication();
//                    };
//                }
//            }
//            else
//            {
//                app.UseExceptionHandler("/Home/Error");
//                app.UseHsts();
//            }

//            app.UseHttpsRedirection();
//            app.UseStaticFiles();
//            app.UseRouting();
//            //app.UseAuthentication();   // <-- ADD if Identity used
//            //app.UseAuthorization();

//            // ---- let every module add its own routes / static files ----
//            var modules = app.ApplicationServices.GetServices<IModule>();
//            foreach (var m in modules)
//                m.Configure(app, env);

//            // ---- route to settings and main stuff ----
//            app.UseEndpoints(endpoints =>
//            {
//                endpoints.MapControllers(); // attribute-routed controllers
//                endpoints.MapControllerRoute(
//                        name: "default",
//                        pattern: "{controller=Home}/{action=Index}/{id?}",
//                        defaults: new { controller = "Home", action = "Index" });

//                // Diagnostic route to prove HomeController is discoverable
//                endpoints.MapGet("/diag/home", ctx =>
//                    ctx.Response.WriteAsync("HomeController is alive"));
//            }); 
//        });
//    })
//    .Build();


//PrestartInit(host);

//var modules = host.Services.GetServices<IModule>();
//foreach (var m in modules)
//    m.PrestartInit(host);

//await host.RunAsync();

//void PrestartInit(IHost host)
//{
//    using (var scope = host.Services.CreateScope())
//    {
//        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
//        //db.Database.EnsureCreated();
//        db.Database.Migrate();
//        db.Database.ExecuteSqlRaw("PRAGMA journal_mode = WAL;");
//        db.Database.ExecuteSqlRaw("PRAGMA synchronous = NORMAL;");
//        db.Database.ExecuteSqlRaw("PRAGMA busy_timeout = 5000;");
//    }
//}
