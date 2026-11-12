using System.Text.Json;

using Common.Infrastructure;
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
        else if (await TrySeedAdminAsync(userManager, roleManager))
        {
            adminState.SetAdminConfigured();
        }
        else
        {
            _logger.LogInformation("admin setup enabled");
        }
        adminState.SetAdminChecked();

        _logger.LogInformation("admin checked");
    }

    /// <summary>File in the node home that creates the Admin user on a fresh database (used by the swarm setup).</summary>
    public const string AdminSeedFileName = "admin-seed.json";

    private sealed class AdminSeed
    {
        public string Name { get; set; } = "Admin";
        public string Surname { get; set; } = "";
        public string Email { get; set; } = "";
        public string Password { get; set; } = "";
    }

    /// <summary>
    /// A node with no Admin yet and an admin-seed.json in its node home creates the Admin user from it (same as the
    /// first-run admin registration page) and deletes the file, so the password doesn't stay on disk. The user gets
    /// this node's own keys: the key chain is created for this database, not copied from anywhere.
    /// </summary>
    private async Task<bool> TrySeedAdminAsync(UserManager<ApplicationUser> userManager, RoleManager<IdentityRole> roleManager)
    {
        string path = Path.Combine(NodeHome.Folder, AdminSeedFileName);
        if (!File.Exists(path)) return false;

        AdminSeed? seed;
        try
        {
            seed = JsonSerializer.Deserialize<AdminSeed>(await File.ReadAllTextAsync(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Admin seed] {Path} is not valid JSON; admin not created.", path);
            return false;
        }
        if (seed == null || string.IsNullOrEmpty(seed.Password))
        {
            _logger.LogError("[Admin seed] {Path} has no password; admin not created.", path);
            return false;
        }

        var user = new ApplicationUser
        {
            UserName = "Admin",
            Name = string.IsNullOrWhiteSpace(seed.Name) ? "Admin" : seed.Name,
            Surname = seed.Surname ?? "",
            Email = seed.Email ?? "",
            DisplayNickname = "Admin",
            IsEncrypted = false
        };

        var result = await userManager.CreateAsync(user, seed.Password);
        if (!result.Succeeded)
        {
            _logger.LogError("[Admin seed] Admin not created: {Errors}", string.Join("; ", result.Errors.Select(e => e.Description)));
            return false;
        }

        user = await userManager.FindByNameAsync("Admin");
        foreach (var role in roleManager.Roles.Select(r => r.Name).ToList())
        {
            if (!string.IsNullOrEmpty(role) && !await userManager.IsInRoleAsync(user, role))
                await userManager.AddToRoleAsync(user, role);
        }

        try { File.Delete(path); } catch (Exception ex) { _logger.LogWarning(ex, "[Admin seed] Could not delete {Path}; delete it by hand.", path); }
        _logger.LogInformation("[Admin seed] Admin user created from {File}.", AdminSeedFileName);
        return true;
    }
}