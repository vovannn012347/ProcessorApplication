using System.Security.Claims;
using System.Text;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

using ProcessorApplication.Attributes;
using ProcessorApplication.Database;
using ProcessorApplication.Database.Models;
using ProcessorApplication.Infrastructure;
using ProcessorApplication.Services;
using ProcessorApplication.Services.User;
using ProcessorApplication.Utils;
using ProcessorApplication.ViewModels.User;

namespace ProcessorApplication.Controllers;

[Authorize]
[SessionKeyRequired("UselessInfoHash")]
[ModuleRoute("Main")]
[Route("[controller]/[action]/{id?}")]
public class ProfileController : Controller
{
    private readonly AppDbContext _dbContext;
    private readonly ProcessorApplicationUserManager _userManager;
    private readonly SignInManager<ApplicationUser> _signInManager;
    private readonly RoleManager<IdentityRole> _roleManager;
    private readonly IdentityPortabilityHandler _identityExporter;
    private readonly IEmailService _emailService;

    public ProfileController(
        AppDbContext dbContext,
        ProcessorApplicationUserManager userManager,
        SignInManager<ApplicationUser> signInManager,
        RoleManager<IdentityRole> roleManager,
        IdentityPortabilityHandler identityExporter,
        IEmailService emailService
        )
    {
        _dbContext = dbContext;
        _userManager = userManager;
        _signInManager = signInManager;
        _roleManager = roleManager;
        _identityExporter = identityExporter;
        _emailService = emailService;
    }

    private bool IsEffectiveAdmin()
    {
        // 1. Check actual Role
        if (User.IsInRole("Admin")) return true;

        // 2. Check Hardcoded "SuperUser" (username "admin")
        // This allows the user "admin" to manage roles even if they removed the role from themselves.
        var name = User.Identity?.Name;
        if (!string.IsNullOrEmpty(name) && name.Equals("admin", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    [HttpGet]
    public async Task<IActionResult> Index(string userName = "")
    {
        ApplicationUser user;

        // Check permissions to view other users
        // Hardcoded "admin" username bypass or Admin role
        //bool isSuperUser = User.Identity.Name?.Equals("admin", StringComparison.OrdinalIgnoreCase) ?? false;
        //bool isAdmin = User.IsInRole("Admin") || isSuperUser;


        bool isAdmin = IsEffectiveAdmin();

        if (!string.IsNullOrEmpty(userName) && isAdmin)
        {
            user = await _userManager.FindByNameAsync(userName);
        }
        else
        {
            user = await _userManager.GetUserAsync(User);
        }

        if (user == null) return NotFound();

        // 1. Detach to prevent accidental saving of decrypted state during View
        _dbContext.Entry(user).State = EntityState.Detached;

        bool isAuditView = false;
        string decryptedSensitiveData = string.Empty;

        var currentUserId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        bool isSelf = (user.Id == currentUserId);

        // 2. Decryption Logic
        if (isSelf)
        {
            var sessionKey = HttpContext.Session.GetString("UselessInfoHash");
            if (!string.IsNullOrEmpty(sessionKey))
            {
                _userManager.DecryptUserDataDirect(user, sessionKey);
            }
        }
        else 
        if (isAdmin)
        {
            isAuditView = true;
            // Admin View: Triggers Audit
            if (!_userManager.DecryptUserDataByServer(user))
            {
                ModelState.AddModelError("", "Cannot notify user. Will not decrypt.");
            }
        }

        // 3. Prepare ViewModel
        var userRoles = await _userManager.GetRolesAsync(user);
        var allRoles = await _roleManager.Roles.AsNoTracking().ToListAsync();

        var roleSelections = allRoles.Select(role => new RoleSelectionViewModel
        {
            RoleName = role.Name,
            IsSelected = userRoles.Contains(role.Name)
        }).ToList();

        var model = new ProfileViewModel
        {
            User = user,
            UserSecurity = user.IsEncrypted ? null : user.SecurityPreferences,
            RoleSelections = roleSelections,
            IsAuditView = isAuditView,
            CanEditRoles = isAdmin // Only admins can edit roles
        };

        if(ModelState.ErrorCount > 0)
        {
            user.Id = Guid.Empty.ToString();
        }

        return Request.IsAjaxRequest() ? PartialView("Index", model) : View("Index", model);
    }

    [HttpPost]
    public async Task<IActionResult> Update(ProfileViewModel model)
    {
        var user = await _userManager.FindByIdAsync(model.User.Id);
        if (user == null)
        {
            ModelState.AddModelError("", "Cannot save changes.");
            return Request.IsAjaxRequest() ? PartialView("Index", model) : View("Index", model);
        }

        var currentUserId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        bool isSelfEdit = (user.Id == currentUserId);

        // Determine Permissions
        bool isAdmin = IsEffectiveAdmin();
       
        if (!isSelfEdit && !isAdmin)
        {
            ModelState.AddModelError("", "Forbidden.");
            return Request.IsAjaxRequest() ? PartialView("Index", model) : View("Index", model);
        }

        bool keyLoaded = false;
        if (isSelfEdit)
        {
            var userKey = HttpContext.Session.GetString("UselessInfoHash");
            if (!string.IsNullOrEmpty(userKey))
                keyLoaded = _userManager.DecryptUserDataDirect(user, userKey);
        }
        else if (isAdmin)
        {
            keyLoaded = _userManager.LoadKeyForAdminAction(user);
            if (keyLoaded && user.IsEncrypted)
                _userManager.DecryptUserDataDirect(user, user.EncryptionHash);
        }

        if (!keyLoaded)
        {
            ModelState.AddModelError("", "Decryption failed. Cannot save changes.");
            model.CanEditRoles = isAdmin;
            return Request.IsAjaxRequest() ? PartialView("Index", model) : View("Index", model);
        }

        user.Name = model.User.Name;
        user.Surname = model.User.Surname;
        user.DisplayNickname = model.User.DisplayNickname;
        user.Email = model.User.Email;

        if (isSelfEdit && model.UserSecurity != null)
        {
            user.SecurityPreferences = model.UserSecurity;
        }

        if (isAdmin)
        {
            var currentRoles = await _userManager.GetRolesAsync(user);

            var selectedRoles = model.RoleSelections
                .Where(x => x.IsSelected)
                .Select(x => x.RoleName)
                .ToList();

            var rolesToAdd = selectedRoles.Except(currentRoles).ToList();
            var rolesToRemove = currentRoles.Except(selectedRoles).ToList();

            if (rolesToAdd.Any()) await _userManager.AddToRolesAsync(user, rolesToAdd);
            if (rolesToRemove.Any()) await _userManager.RemoveFromRolesAsync(user, rolesToRemove);
        }

        var result = await _userManager.UpdateAsync(user);

        if (result.Succeeded)
        {
            TempData["StatusMessage"] = "Profile updated successfully.";
            return RedirectToAction("Index", new { userName = isAdmin && !isSelfEdit ? user.UserName : "" });
        }

        foreach (var error in result.Errors) ModelState.AddModelError("", error.Description);
        return Request.IsAjaxRequest() ? PartialView("Index", model) : View("Index", model);
    }

    [HttpGet]
    public async Task<IActionResult> Download(string userName)
    {
        ApplicationUser user;
        if (!string.IsNullOrEmpty(userName))
        {
            user = await _userManager.FindByNameAsync(userName);
        }
        else
        {
            user = await _userManager.GetUserAsync(User);
        }
        if (user == null) return NotFound();

        var currentUserId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        bool isSelfDownload = (user.Id == currentUserId);
        bool isAdmin = User.IsInRole("Admin");

        if (!isSelfDownload && !isAdmin) return Forbid();

        string sessionKey = string.Empty;

        if (isSelfDownload)
        {
            sessionKey = HttpContext.Session.GetString("UselessInfoHash");
        }
        else if (isAdmin)
        {
            if (_userManager.DecryptUserDataByServer(user))
            {
                sessionKey = user.EncryptionHash;
            }
            else
            {
                TempData["StatusMessage"] = "Cannot notify user. Will not decrypt.";
                return RedirectToAction("Index");
            }
        }

        if (string.IsNullOrEmpty(sessionKey))
        {
            TempData["StatusMessage"] = "Export failed: Could not decrypt security keys.";
            return RedirectToAction("Index");
        }

        var tempPath = Path.Combine(Path.GetTempPath(), "Export_" + Guid.NewGuid());
        Directory.CreateDirectory(tempPath);

        try
        {
            var filePath = await _identityExporter.ProcessExportAsync(
                user.UserName, 
                new List<string>(){ "profile_main" },
                sessionKey, 
                tempPath);

            if (filePath != null && filePath.Count > 0 && !System.IO.File.Exists(Path.Combine(tempPath, filePath[0]))) 
                return NotFound("Export generation failed.");

            var fileBytes = await System.IO.File.ReadAllBytesAsync(Path.Combine(tempPath, filePath[0]));
            var fileName = $"Identity_{user.UserName}_{DateTime.Now:yyyyMMdd}.json";

            return File(fileBytes, "application/json", fileName);
        }
        finally
        {
            if (Directory.Exists(tempPath)) Directory.Delete(tempPath, true);
        }
    }
    

    [HttpPost]
    public IActionResult CheckConnectivity()
    {
        bool isHealthy = _emailService.IsHealthy;
        return Json(new { success = isHealthy, message = isHealthy ? "Email Service is connected." : "Email Service unreachable." });
    }
}