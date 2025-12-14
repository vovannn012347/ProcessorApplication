

using Common.Interfaces;
using Common.Interfaces.Menu;
using Common.Models;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

using ProcessorApplication.Configuration;
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

    public virtual IEnumerable<string> GetDefinedRoles()
    {
        return new[] { "Admin", "User" };
    }
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
                opt.UseSqlite(sqliteCs, sqliteOptions => sqliteOptions.CommandTimeout(30)));
        }

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

        // portability
        services.AddScoped<IdentityPortabilityHandler>();
    }

    public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
    {
        app.UseEndpoints(endpoints =>
            {
                endpoints.MapControllerRoute(
                        name: "default",
                        pattern: "Main/{controller=Home}/{action=Dashboard}/{id?}",
                        defaults: new { controller = "Home", action = "Dashboard" });
            });

    }

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
