using Common.Infrastructure;
using Common.Models;

using Infrastructure.Monitoring;

using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc.Razor;
using Microsoft.AspNetCore.Mvc.Razor.RuntimeCompilation;

using ProcessorApplication;
using ProcessorApplication.Attributes;
using ProcessorApplication.Configuration;
using ProcessorApplication.Configuration.SettingsIngest;
using ProcessorApplication.Infrastructure;
using ProcessorApplication.Infrastructure.Web;
using ProcessorApplication.Services;
using ProcessorApplication.Utils;

// This node's own folder (databases, Module_Data, its settings files): --node-home <folder> or PROCESSOR_NODE_HOME.
// Becomes the current directory, so every relative data path lands in it. Without it: the app folder, as before.
NodeHome.Initialize(args);

// Everything the console shows also goes to <node home>/logs/console-<date>.log (off: --no-console-log).
ProcessorApplication.Infrastructure.Logging.ConsoleFileTee.Start(Path.Combine(NodeHome.Folder, "logs"), args);

// Console title = node home name, so the windows of several nodes (swarm, Tracker1/Client1...) can be told apart
if (OperatingSystem.IsWindows() && !Console.IsOutputRedirected && NodeHome.IsSeparate)
{
    try { Console.Title = $"{Path.GetFileName(NodeHome.Folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))} - ProcessorApplication"; }
    catch { /* no console window */ }
}

// Dots-only decimals: numbers are shown and read back the same way on every machine, whatever the Windows regional
// format is (with a comma format, "0,05" in a number field shows empty and the browser's "0.05" fails to bind).
// Only number/date FORMATTING changes; the UI language (CurrentUICulture) is left as it is.
System.Globalization.CultureInfo.DefaultThreadCurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;

const string ModuleDir = "Modules";
var modulesRoot = Path.Combine(AppContext.BaseDirectory, ModuleDir);
if (!Directory.Exists(modulesRoot)) Directory.CreateDirectory(modulesRoot);

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    // Program files (views, wwwroot) are always the app folder's, also when the current directory is a node home.
    ContentRootPath = AppContext.BaseDirectory,
});

builder.Configuration
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
    .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: true, reloadOnChange: true);

if (NodeHome.IsSeparate)
{
    // The node's own copies override the shared ones.
    builder.Configuration
        .AddJsonFile(Path.Combine(NodeHome.Folder, "appsettings.json"), optional: true, reloadOnChange: true)
        .AddJsonFile(Path.Combine(NodeHome.Folder, $"appsettings.{builder.Environment.EnvironmentName}.json"), optional: true, reloadOnChange: true);
}

builder.Configuration
    // Re-add these after the JSON files above, so environment variables and command-line arguments override
    // appsettings.json again (CreateBuilder loads them before, and the re-added files would win otherwise).
    // Needed e.g. for "--SettingsIngest:Mode=Overwrite" and for headless nodes configured by environment.
    .AddEnvironmentVariables()
    .AddCommandLine(args);

// 2. Load Module JSON Files (Prefixed with Area)
// This adds "Main:SecuritySettings:..." from the file to the config
// App folder first, then the node home's files (same names and places) on top.
builder.Configuration.AddLayeredModuleJsonFiles(builder.Environment.EnvironmentName);

// A node environment (NodeTracker1Debug...) with no settings file for it anywhere means the program folder was built
// with a different configuration than the launch profile expects (e.g. built NodeTrackerSwarm, started the Tracker1
// profile): the node would silently run on the shared defaults. Say so loudly.
if (builder.Environment.EnvironmentName.StartsWith("Node", StringComparison.OrdinalIgnoreCase))
{
    var envName = builder.Environment.EnvironmentName;
    bool anyNodeFile = ModuleConfigurationExtensions.GetLayeredModuleJsonFiles(envName)
        .Any(f => Path.GetFileName(f.FilePath).Contains("." + envName + ".", StringComparison.OrdinalIgnoreCase));
    if (!anyNodeFile)
    {
        var previous = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.Error.WriteLine($"[Node home] No settings files for environment '{envName}' in the program folder ({NodeHome.AppFolder})");
        Console.Error.WriteLine($"[Node home] or the node home ({NodeHome.Folder}). The program folder was probably built with another");
        Console.Error.WriteLine($"[Node home] configuration: build configuration '{envName}' and start again. Running on the shared defaults.");
        Console.ForegroundColor = previous;
    }
}

var modules = ModuleLoader.DiscoverModules(builder.Services, builder.Configuration, AppContext.BaseDirectory);

ModuleRegistry.InstallResolutionDivert(); //important, do not touch

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
        // Remove the default provider that uses reflection on Default ALC
        //var featureProvider = apm.FeatureProviders.OfType<ControllerFeatureProvider>().FirstOrDefault();
        //if (featureProvider != null) apm.FeatureProviders.Remove(featureProvider);

        //// Add our custom provider that forces scan inside isolated ALCs
        //apm.FeatureProviders.Add(new ModuleControllerFeatureProvider());
    });

builder.Services.AddRazorPages().AddRazorRuntimeCompilation();
builder.Services.Configure<MvcRazorRuntimeCompilationOptions>(options =>
{
    // Clear default providers (stops looking at the source code folder)
    //options.FileProviders.Clear();

    options.FileProviders.Add(new Microsoft.Extensions.FileProviders.PhysicalFileProvider(AppContext.BaseDirectory));

    // 2. Explicitly add each Module's folder to the provider chain
    var modulesPath = Path.Combine(AppContext.BaseDirectory, "Modules");
    if (Directory.Exists(modulesPath))
    {
        foreach (var dir in Directory.GetDirectories(modulesPath))
        {
            options.FileProviders.Add(new Microsoft.Extensions.FileProviders.PhysicalFileProvider(dir));
        }
    }

    // 3. Register module assemblies into the Compilation metadata reference list
    // This allows the Razor compiler to resolve types (@model, etc.) across ALC boundaries
    foreach (var alc in ProcessorApplication.Infrastructure.ModuleRegistry.Contexts)
    {
        foreach (var assembly in alc.Assemblies)
        {
            var assemblyPath = assembly.Location;
            // Only add paths that exist and aren't already covered by the host's primary runtime
            if (!string.IsNullOrEmpty(assemblyPath) && File.Exists(assemblyPath))
            {
                options.AdditionalReferencePaths.Add(assemblyPath);
            }
        }
    }
});

builder.Services.AddSingleton<IModuleService, ModuleService>();
// First Admin from a file, for headless / mass deployment (see AdminSeedFile): --AdminFile <path>, AdminFile=<path>,
// admin.json in the node home, or admin.json in the program folder (shared by all nodes started from it)
var adminSeedFile = ProcessorApplication.Services.User.AdminSeedFile.Locate(builder.Configuration[ProcessorApplication.Services.User.AdminSeedFile.SettingKey]);
if (adminSeedFile.Exists)
{
    Console.WriteLine($"[Admin seed] Admin file found: {adminSeedFile.Path} (used only if this node has no Admin yet).");
}
builder.Services.AddSingleton(adminSeedFile);

// No Windows Event Log: the default host logs every warning there too (Event Viewer "Application" filling up with
// node warnings, and a slow synchronous write per warning - noticeable with 20 swarm nodes on one PC). Console,
// Debug and EventSource stay as the defaults had them. Logging:EventLog:Enabled = true brings it back.
if (!builder.Configuration.GetValue("Logging:EventLog:Enabled", false))
{
    builder.Logging.ClearProviders();
    builder.Logging.AddConsole();
    builder.Logging.AddDebug();
    builder.Logging.AddEventSourceLogger();
}

// EF Core's SQL statements go to <node home>/logs/sql-<date>.log, not the console (Logging:Console keeps them at
// Warning). Off with Logging:SqlFile:Enabled = false.
if (builder.Configuration.GetValue("Logging:SqlFile:Enabled", true))
{
    builder.Logging.AddProvider(new ProcessorApplication.Infrastructure.Logging.CategoryFileLoggerProvider(
        Path.Combine(NodeHome.Folder, "logs"), "sql", "Microsoft.EntityFrameworkCore"));
}

builder.Services.AddScoped<ISettingsIngestService, SettingsIngestService>();
builder.Services.AddScoped<ISettingsStore, SettingsStore>();
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

builder.Services.Configure<RazorViewEngineOptions>(options =>
{
    options.ViewLocationExpanders.Add(new UniversalViewLocationExpander());
});


var aggregatedCapabilities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);


foreach (var m in modules)
{
    builder.Services.AddSingleton(m);

    var assembly = m.GetType().Assembly;
    mvcBuilder.AddApplicationPart(assembly);


    // Statically harvest advertised capabilities from the module descriptor
    var capabilities = m.ListAdvertisedCapabilities();
    if (capabilities != null)
    {
        foreach (var cap in capabilities)
        {
            if (!string.IsNullOrWhiteSpace(cap))
            {
                aggregatedCapabilities.Add(cap);
            }
        }
    }

    m.ConfigureServices(builder.Services, builder.Configuration.GetSection(m.ModuleId));
}

// Register the strongly-typed AdvertisedCapabilitiesRegistry container as a Singleton
var capabilitiesRegistry = new AdvertisedCapabilitiesRegistry(aggregatedCapabilities);
builder.Services.AddSingleton(capabilitiesRegistry);
builder.Services.AddSingleton<UserPresenceStore>();

// build
var app = builder.Build();

// prestart init actions
var moduleInstances = app.Services.GetServices<IModule>();

foreach (var m in moduleInstances)
    m.PrestartInit(app);

await ConfigurationBootstrapper.SeedAsync(app, moduleInstances);

// Re-ingest edited settings files (SettingsIngest:Mode, default "Changed"); see SettingsIngestService.
await SettingsIngestStartup.RunAsync(app);

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

    foreach(var module in modules)
    {
        module.ConfigureEndpoints(endpoints);
    }
});



await app.RunAsync();


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
/*
var endpointDataSource = app.Services.GetRequiredService<EndpointDataSource>();

Console.WriteLine("==========================================================");
Console.WriteLine("[DI ROUTE EXPLORER] REGISTERED ENDACTION MATRICES");
Console.WriteLine("==========================================================");

foreach (var endpoint in endpointDataSource.Endpoints.OfType<RouteEndpoint>())
{
    var displayName = endpoint.DisplayName;
    var routePattern = endpoint.RoutePattern.RawText;
    var order = endpoint.RoutePattern.InboundPrecedence;

    // Extract bound routing parameters (like controller defaults, areas, actions)
    var defaults = string.Join(", ", endpoint.RoutePattern.Defaults.Select(d => $"{d.Key}={d.Value}"));

    Console.WriteLine($"Pattern: /{routePattern}");
    Console.WriteLine($"   Handler: {displayName}");
    Console.WriteLine($"   Defaults:  [{defaults}]");
    Console.WriteLine("----------------------------------------------------------");
}
Console.WriteLine("==========================================================");
*/