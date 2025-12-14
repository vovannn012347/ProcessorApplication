using ProcessorApplication;
using ProcessorApplication.Infrastructure;
using ProcessorApplication.Services;
using ProcessorApplication.Utils;

const string ModuleDir = "Modules";
var modulesRoot = Path.Combine(AppContext.BaseDirectory, ModuleDir);
if (!Directory.Exists(modulesRoot)) Directory.CreateDirectory(modulesRoot);

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
builder.Configuration
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
    .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: true, reloadOnChange: true);

// 2. Load Module JSON Files (Prefixed with Area)
// This adds "Main:SecuritySettings:..." from the file to the config
builder.Configuration.AddModuleJsonFiles(AppContext.BaseDirectory);

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

var modules = ModuleLoader.DiscoverModules(AppContext.BaseDirectory);
var main = (IModule)Activator.CreateInstance(typeof(MainModule))!;
modules = modules.Prepend(main);

foreach (var m in modules)
{
    foreach (var source in m.GetConfigurationSources(builder.Configuration.GetSection(m.ModuleId)))
    {
        ((IConfigurationBuilder)builder.Configuration).Add(source);
    }
}

// core services
builder.Services.AddControllersWithViews()
    .AddRazorRuntimeCompilation(options =>
    {
        // Add the Modules directory to the list of places Razor looks for files
        if (Directory.Exists(modulesRoot))
        {
            options.FileProviders.Add(
                new Microsoft.Extensions.FileProviders.PhysicalFileProvider(modulesRoot)
            );
        }
    });

builder.Services.AddSingleton<IModuleService, ModuleService>();
builder.Services.AddMemoryCache();
builder.Services.AddSession(options =>
{
    // Set session timeout (e.g., 20 minutes)
    options.IdleTimeout = TimeSpan.FromMinutes(20);
    options.Cookie.IsEssential = true;
    options.Cookie.MaxAge = TimeSpan.FromMinutes(20);
    options.Cookie.HttpOnly = true;
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
});

// module discovery and registration
foreach (var m in modules)
{
    builder.Services.AddSingleton(m);
    m.ConfigureServices(builder.Services, builder.Configuration.GetSection(m.ModuleId));
}

// build
var app = builder.Build();

// prestart init actions
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
app.UseSession();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

// Let modules configure middleware, routes, static files
foreach (var m in moduleInstances)
    m.Configure(app, app.Environment);

await app.RunAsync();