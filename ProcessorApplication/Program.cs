using Common.Models;

using Infrastructure.Monitoring;

using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc.Razor;
using Microsoft.AspNetCore.Mvc.Razor.RuntimeCompilation;

using ProcessorApplication;
using ProcessorApplication.Attributes;
using ProcessorApplication.Configuration;
using ProcessorApplication.Infrastructure;
using ProcessorApplication.Infrastructure.Web;
using ProcessorApplication.Services;
using ProcessorApplication.Utils;

const string ModuleDir = "Modules";
var modulesRoot = Path.Combine(AppContext.BaseDirectory, ModuleDir);
if (!Directory.Exists(modulesRoot)) Directory.CreateDirectory(modulesRoot);

var builder = WebApplication.CreateBuilder(args);

builder.Configuration
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
    .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: true, reloadOnChange: true);

// 2. Load Module JSON Files (Prefixed with Area)
// This adds "Main:SecuritySettings:..." from the file to the config
builder.Configuration.AddModuleJsonFiles(AppContext.BaseDirectory, builder.Environment.EnvironmentName);

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