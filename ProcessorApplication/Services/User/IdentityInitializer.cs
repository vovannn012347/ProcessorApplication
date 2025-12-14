using Common.Interfaces;

using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore.Storage;

using ProcessorApplication.Database.Models;
using ProcessorApplication.Infrastructure;
using ProcessorApplication.Services.User;

namespace ProcessorApplication.Services.Settings;

public class IdentityInitializer : BackgroundService
{
    private readonly IServiceProvider _sp;
    private readonly IEnumerable<IModule> _modules;
    private readonly ILogger<IdentityInitializer> _logger;

    public IdentityInitializer(
        IServiceProvider sp,
        IEnumerable<IModule> modules,
        ILogger<IdentityInitializer> log)
    {
        _sp = sp;
        _modules = modules;
        _logger = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Run seeding after startup tasks are complete
        //await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);

        using var scope = _sp.CreateScope();

        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var adminState = scope.ServiceProvider.GetRequiredService<AdminSetupState>();

        _logger.LogInformation("checking roles");

        foreach (var module in _modules)
        {
            foreach (var roleName in module.GetDefinedRoles())
            {
                if (!await roleManager.RoleExistsAsync(roleName))
                {
                    _logger.LogInformation("Seeding Role '{Role}' for Module '{Module}'", roleName, module.Name);
                    await roleManager.CreateAsync(new IdentityRole(roleName));
                }
            }
        }

        // Ensure core Admin role always exists
        if (!await roleManager.RoleExistsAsync("Admin"))
        {
            await roleManager.CreateAsync(new IdentityRole("Admin"));
        }

        _logger.LogInformation("role check end");

        
        _logger.LogInformation("checking admin...");

        var admin = await userManager.FindByNameAsync("Admin");

        if (admin != null)
        {
            adminState.SetAdminConfigured();
            if(!(await userManager.IsInRoleAsync(admin, "Admin")))
            {
                await userManager.AddToRoleAsync(admin, "Admin");
            }
        }
        else
        {
            _logger.LogInformation("admin setup enabled");
        }
        adminState.SetAdminChecked();

        _logger.LogInformation("admin checked");
    }
}