
using Infrastructure.Monitoring;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using ProcessorApplication;
using ProcessorApplication.Attributes;
using ProcessorApplication.Dashboard;
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

//if (Directory.Exists(modulesRoot))
//{
//    foreach (var dir in Directory.GetDirectories(modulesRoot))
//    {
//        var moduleName = Path.GetFileName(dir);
//        var file = Path.Combine(dir, $"appsettings.{moduleName}.json");
//        if (File.Exists(file))
//            builder.Configuration.AddJsonFile(file, optional: true, reloadOnChange: true);
//    }
//}

var modules = ModuleLoader.DiscoverModules(AppContext.BaseDirectory);
var main = (IModule)Activator.CreateInstance(typeof(MainModule))!;
modules = modules.Prepend(main).ToList();

foreach (var m in modules)
{
    foreach (var source in m.GetConfigurationSources(builder.Configuration.GetSection(m.ModuleId)))
    {
        ((IConfigurationBuilder)builder.Configuration).Add(source);
    }
}

// core services
var mvcBuilder = builder.Services.AddControllersWithViews(options =>
    {
        options.Conventions.Add(new ModuleRoutingConvention());
    })
    .ConfigureApplicationPartManager(apm =>
    {
        // Your custom code goes here
    });
builder.Services.AddRazorPages();

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

    var assembly = m.GetType().Assembly;
    mvcBuilder.AddApplicationPart(assembly);
    m.ConfigureServices(builder.Services, builder.Configuration.GetSection(m.ModuleId));
}

builder.Services.AddSingleton<UserPresenceStore>();

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

app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
});

// request pipeline
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Main/Home/Error");
    app.UseHsts();
}

if (builder.Configuration.GetValue<bool>("Features:ForceHttpsRedirection"))
{
    app.UseHttpsRedirection();
}

app.UseStaticFiles(); // Global app wwwroot
app.UseSession();

// 3. MODULE ISOLATION (Static Files & Isolated Config only)
foreach (var m in moduleInstances)
{
    m.Configure(app, app.Environment);
}

// 4. GLOBAL ROUTING (The Single Source of Truth)
app.UseRouting();

// 5. GLOBAL AUTHENTICATION & AUTHORIZATION
// One instance handles the whole app. Use [AllowAnonymous] on Login actions.
app.UseAuthentication();
app.UseMiddleware<UserPresenceMiddleware>();
app.UseAuthorization();

// 4. CENTRALIZED ROUTING (Unified Table)
app.UseEndpoints(endpoints =>
{
    // Maps all Module-prefixed controllers (Main/Account, Processor/Settings)
    endpoints.MapControllers();
    endpoints.MapRazorPages();

    endpoints.MapHub<DashboardHub>("/dashboardHub");

    // Root Redirect
    endpoints.MapGet("/", context => {
        context.Response.Redirect("/Main/Home/Dashboard");
        return Task.CompletedTask;
    });

}); 


/*
var logger = app.Services
    .GetRequiredService<ILogger<Program>>();

var endpointDataSource =
    app.Services.GetRequiredService<EndpointDataSource>();

foreach (var endpoint in endpointDataSource.Endpoints.OfType<RouteEndpoint>())
{
    var methods = endpoint.Metadata
        .OfType<HttpMethodMetadata>()
        .FirstOrDefault()?.HttpMethods;

    logger.LogInformation(
        "Route: {Methods} {Route}",
        methods is null ? "ANY" : string.Join(",", methods),
        endpoint.RoutePattern.RawText);
}*/

await app.RunAsync();