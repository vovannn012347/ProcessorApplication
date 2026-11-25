using System.Text.Json;

using Common.Infrastructure;
using Common.Interfaces;

using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;

using ProcessorApplication.Configuration.Settings;
using ProcessorApplication.Database.Models;
using ProcessorApplication.Infrastructure;
using ProcessorApplication.Services.User;

namespace ProcessorApplication.Services.Settings;

public class IdentityInitializer : BackgroundService
{
    private readonly IServiceProvider _sp;
    private readonly IEnumerable<IModule> _modules;
    private readonly ILogger<IdentityInitializer> _logger;
    private readonly AdminSeedFile _seedFile;

    public IdentityInitializer(
        IServiceProvider sp,
        IEnumerable<IModule> modules,
        AdminSeedFile seedFile,
        ILogger<IdentityInitializer> log)
    {
        _sp = sp;
        _modules = modules;
        _seedFile = seedFile;
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

        if (_seedFile.MissingExplicitPath != null)
        {
            _logger.LogWarning("[Admin seed] {Setting} points to {Path}, but there is no file there.", AdminSeedFile.SettingKey, _seedFile.MissingExplicitPath);
        }

        if (admin != null)
        {
            if (_seedFile.Exists)
            {
                _logger.LogWarning("[Admin seed] Admin already exists, {Path} ignored. It holds a password in clear text: delete it once every node has started.", _seedFile.Path);
            }
            adminState.SetAdminConfigured();
            if(!(await userManager.IsInRoleAsync(admin, "Admin")))
            {
                await userManager.AddToRoleAsync(admin, "Admin");
            }
        }
        else if (await TrySeedAdminAsync(scope.ServiceProvider, userManager, roleManager))
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

    private sealed class AdminSeed
    {
        public string Name { get; set; } = "Admin";
        public string Surname { get; set; } = "";
        public string Email { get; set; } = "";
        public string Password { get; set; } = "";
        /// <summary>Overrides the default (node home: delete; shared file: keep).</summary>
        public bool? DeleteAfterUse { get; set; }
    }

    /// <summary>
    /// A node with no Admin yet creates the Admin user from the admin file (see <see cref="AdminSeedFile"/>), the same way
    /// as the first-run admin registration page: all roles, the node's own keys, and the e-mail as sender address if
    /// none is set. A file in the node home is deleted afterwards; a shared one is kept (other nodes still need it).
    /// </summary>
    private async Task<bool> TrySeedAdminAsync(IServiceProvider services, UserManager<ApplicationUser> userManager, RoleManager<IdentityRole> roleManager)
    {
        if (!_seedFile.Exists) return false;
        string path = _seedFile.Path;

        AdminSeed? seed;
        try
        {
            seed = JsonSerializer.Deserialize<AdminSeed>(await File.ReadAllTextAsync(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
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
            _logger.LogError("[Admin seed] Admin not created from {Path}: {Errors}", path, string.Join("; ", result.Errors.Select(e => e.Description)));
            return false;
        }

        user = await userManager.FindByNameAsync("Admin");
        foreach (var role in roleManager.Roles.Select(r => r.Name).ToList())
        {
            if (!string.IsNullOrEmpty(role) && !await userManager.IsInRoleAsync(user, role))
                await userManager.AddToRoleAsync(user, role);
        }

        // Same as the registration page: the admin's e-mail becomes the sender address if none is configured yet
        try
        {
            var email = services.GetService<IOptionsMonitor<EmailSettings>>();
            string current = email?.Get(MainModule.MODULE_ID).Identity.Email ?? "";
            if (!string.IsNullOrEmpty(seed.Email) && string.IsNullOrEmpty(current))
            {
                await services.GetRequiredService<ISettingService>().SetAsync(MainModule.MODULE_ID, "EmailSettings:Email", seed.Email);
            }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "[Admin seed] Could not store the admin e-mail as sender address."); }

        bool delete = seed.DeleteAfterUse ?? !_seedFile.IsShared;
        if (delete)
        {
            try { File.Delete(path); _logger.LogInformation("[Admin seed] Admin user created from {Path}; file deleted.", path); }
            catch (Exception ex) { _logger.LogWarning(ex, "[Admin seed] Admin user created, but {Path} could not be deleted; delete it by hand.", path); }
        }
        else
        {
            _logger.LogWarning("[Admin seed] Admin user created from shared file {Path} (kept for the other nodes). It holds a password in clear text: delete it once every node has started.", path);
        }
        return true;
    }
}
