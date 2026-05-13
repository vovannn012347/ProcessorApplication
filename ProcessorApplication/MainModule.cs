

using System.Reflection;

using Common.Interfaces;
using Common.Interfaces.Menu;
using Common.Models;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;

using ProcessorApplication.Configuration;
using ProcessorApplication.Configuration.Settings;
using ProcessorApplication.Dashboard;
using ProcessorApplication.Database;
using ProcessorApplication.Database.Models;
using ProcessorApplication.Infrastructure;
using ProcessorApplication.Models.Settings;
using ProcessorApplication.Models.User;
using ProcessorApplication.Policy;
using ProcessorApplication.Services;
using ProcessorApplication.Services.Email;
using ProcessorApplication.Services.HashStamps;
using ProcessorApplication.Services.Settings;
using ProcessorApplication.Services.User;
using ProcessorApplication.Utils;

namespace ProcessorApplication;
public class MainModule : IModule
{
    public const string MainId = "Main";
    public string Name => "Main module";
    public string ModuleId => MainId;

    public Version Version => new Version("1.0.0.0");

    public ModuleDependency[] Dependencies => new ModuleDependency[] { };

    public virtual IEnumerable<string> GetDefinedRoles() => new[] { "Admin", "User" };

    public IEnumerable<string> GetRequiredRoles() => Array.Empty<string>();
    public List<MenuItemViewModel> GetMenuItems(IServiceProvider services)
    {
        return new List<MenuItemViewModel>
        {
            new MenuItemViewModel { 
                Name = "Dashboard",
                IconClass = "fa-solid fa-table-cells", 
                Url = "/Main/Home/Dashboard"
            },
            new MenuItemViewModel {
                Name = "Profile",
                IconClass = "fa-solid fa-user",
                Url = "/Main/Profile/Index",
                Roles = "User,Admin"
            },
            new MenuItemViewModel { 
                Name = "Settings", 
                IconClass = "fa-solid fa-cog",
                Url = "/Main/Settings/Index",
                Roles = "Admin"
            },
            new MenuItemViewModel { 
                Name = "Users", 
                IconClass = "fa-solid fa-address-book", 
                Url = "/Main/Users/Index",
                Roles = "Admin"
            },
            new MenuItemViewModel {
                Name = "Server log",
                IconClass = "fa-solid fa-file-lines",
                Url = "/Main/Log/Index",
                Roles = "Admin"
            }
        };
    }

    public void ConfigureServices(IServiceCollection services, IConfiguration config)
    {
        //var value = config.GetSection<List<EmailProviderGuesserRule>>("EmailProviderGuessRules");

        //email config
        services.Configure<List<EmailProviderGuesserRule>>(
            config.GetSection("EmailProviderGuessRules"));

        // database
        var sqliteCs = config.GetConnectionString("SQLite");
        if (string.IsNullOrEmpty(sqliteCs))
            sqliteCs = config.GetValue<string>("Main:ConnectionStrings:SQLite");
        if (!string.IsNullOrWhiteSpace(sqliteCs))
        {
            services.AddDbContext<AppDbContext>(opt =>
                opt.UseSqlite(sqliteCs, sqliteOptions => sqliteOptions.CommandTimeout(30)),
                contextLifetime: ServiceLifetime.Scoped,
                optionsLifetime: ServiceLifetime.Singleton);

            //for singletons
            services.AddDbContextFactory<AppDbContext>(options =>
                options.UseSqlite(sqliteCs, sqliteOptions => sqliteOptions.CommandTimeout(30)));

        }

        //services.AddScoped<IDashboardRepository, DashboardRepository>();
        services.AddScoped<IWidgetProvider, MainWidgetProvider>();
        services.AddScoped<IDashboardRepository, DashboardRepository>();

        // shared settings

        services.AddScoped<ISettingService, SettingService>();
        services.AddHostedService<SettingsInitializer>();
        services.AddModuleSettings<SecuritySettings>(config, ModuleId);
        services.AddModuleSettings<EmailSettings>(config, ModuleId); 
        
        services.AddSingleton<IEmailService, EmailService>();
        services.AddHostedService<EmailHealthMonitor>();

        services.AddScoped<IHashBackupService, HashBackupService>();
        services.AddScoped<IHashStampService, HashStampService>();
        services.AddHostedService<HashGenerationJob>();

        services.AddSingleton<AdminSetupState>(); //admin setup
        services.AddScoped<UserKeyHolder>(); //login stuff
        //services.AddScoped<ILookupProtector, ServerUserDEKprotectorService<ApplicationUser>>();
        services.AddScoped<UserManager<ApplicationUser>, ProcessorApplicationUserManager>();

        services.AddScoped<IAuditHashKeyProvider, ServerSecurityHashProvider>();

        // identity
        services.AddIdentity<ApplicationUser, IdentityRole>(options =>
            {
                //options.Stores.ProtectPersonalData = true;
            })
            //.AddSignInManager<ApplicationSignInManager>()
            .AddEntityFrameworkStores<AppDbContext>()
            .AddDefaultTokenProviders()
            .AddClaimsPrincipalFactory<UserClaimsPrincipalFactory<ApplicationUser, IdentityRole>>()
            .AddRoleManager<RoleManager<IdentityRole>>()
            .AddUserManager<ProcessorApplicationUserManager>()
            .AddRoleStore<RoleStore<IdentityRole, AppDbContext, string>>()
            .AddUserStore<UserStore<ApplicationUser, IdentityRole, AppDbContext, string>>();

        services.AddHostedService<IdentityInitializer>();

        services.AddHttpContextAccessor();
        services.ConfigureApplicationCookie(options =>
        {
            options.SlidingExpiration = true;
            // Set the path where unauthenticated users should be redirected.
            // This solves the 'redirect to /Account/Login' issue.
            options.LoginPath = "/Main/Account/Login";
            options.AccessDeniedPath = "/Main/Account/AccessDenied";
            options.LogoutPath = "/Main/Account/Logout";
        });

        services.AddSingleton<IAuthorizationHandler, LocalhostHandler>();
        services.AddAuthorization(options =>
        {
            options.AddPolicy("LocalhostOnly", policy =>
            {
                policy.Requirements.Add(new LocalhostRequirement());
            });

            options.AddPolicy("AdminLocalPolicy", policy =>
            {
                policy.RequireAuthenticatedUser();
                policy.RequireRole("Admin");
                policy.AddRequirements(new LocalhostRequirement());
            });

            options.FallbackPolicy = options.DefaultPolicy;
        });

        // for export/import module
        services.AddScoped<IPortabilityHandler, IdentityPortabilityHandler>();
        //for identty export
        services.AddScoped<IdentityPortabilityHandler>();


        // dashboard stuff
        services.AddSingleton<IDashboardSessionManager>(sp => sp.GetRequiredService<DashboardSessionManager>());
        services.AddHostedService(sp => sp.GetRequiredService<DashboardSessionManager>());
        services.AddSingleton<DashboardSessionManager>();
        services.AddSignalR();
    }

    public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
    {
        var assemblyLocation = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        var wwwroot = Path.Combine(assemblyLocation!, "wwwroot");

        if (Directory.Exists(wwwroot))
        {
            app.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = new PhysicalFileProvider(wwwroot),
                // This allows <link href="/Main/css/output.css" />
                RequestPath = $"/{MainId}"
            });
        }
    }

    //public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
    //{
    //    var assemblyLocation = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
    //    var wwwroot = Path.Combine(assemblyLocation!, "wwwroot");

    //    if (Directory.Exists(wwwroot))
    //    {
    //        app.UseStaticFiles(new StaticFileOptions
    //        {
    //            FileProvider = new PhysicalFileProvider(wwwroot),
    //            RequestPath = ""
    //        });
    //    }

    //    // 2. Self-Resolving Routing
    //    //app.UseRouting();
    //    //app.UseAuthorization();
    //    //app.UseEndpoints(endpoints =>
    //    //{
    //    //    endpoints.MapControllers();
    //    //});
    //}

    public void PrestartInit(IHost host)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        db.Database.Migrate();

        db.Database.ExecuteSqlRaw("PRAGMA journal_mode = WAL;");
        db.Database.ExecuteSqlRaw("PRAGMA synchronous = NORMAL;");
        db.Database.ExecuteSqlRaw("PRAGMA busy_timeout = 5000;");

        bool adminExists = db.Users.AsNoTracking().FirstOrDefault(u => u.NormalizedUserName == "ADMIN") != null;

        if (!adminExists && host is WebApplication app)
        {
            app.Use(async (context, next) =>
            {
                var state = context.RequestServices.GetRequiredService<AdminSetupState>();
                if (!state.IsAdminConfigured)
                {
                    var connection = context.Connection;
                    bool isLocal = false;

                    if (connection.RemoteIpAddress != null)
                    {
                        if (System.Net.IPAddress.IsLoopback(connection.RemoteIpAddress))
                        {
                            isLocal = true;
                        }
                    }

                    if (isLocal)
                    {
                        var path = context.Request.Path.Value?.ToLower();
                        if (path == null || path.Contains(".") || path.Contains("/main/account"))
                        {
                            await next();
                            return;
                        }

                        // If Admin is NOT configured, redirect to setup
                        context.Response.Redirect("/Main/Account/AdminRegister");
                        return; // Stop the pipeline here
                    }
                }

                await next();
            });
        }
    }

    public IEnumerable<IConfigurationSource> GetConfigurationSources(IConfiguration initialConfig)
    {
        var sqliteCs = initialConfig.GetConnectionString("SQLite");
        if (string.IsNullOrEmpty(sqliteCs))
            sqliteCs = initialConfig.GetValue<string>("Main:ConnectionStrings:SQLite");

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
