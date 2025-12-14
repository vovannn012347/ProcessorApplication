using System.Security.Claims;
using System.Text;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

using ProcessorApplication.Attributes;
using ProcessorApplication.Database;
using ProcessorApplication.Database.Models;
using ProcessorApplication.Services;
using ProcessorApplication.Services.User;
using ProcessorApplication.Utils;
using ProcessorApplication.ViewModels.User;

namespace ProcessorApplication.Controllers;

[Authorize]
[Route("Main/Profile")]
[SessionKeyRequired("UselessInfoHash")]
public class ProfileController : Controller
{
    private readonly AppDbContext _dbContext;
    private readonly ProcessorApplicationUserManager _userManager;
    private readonly SignInManager<ApplicationUser> _signInManager;
    private readonly RoleManager<IdentityRole> _roleManager;
    private readonly IdentityPortabilityHandler _identityExporter;
    
    public ProfileController(
        AppDbContext dbContext,
        ProcessorApplicationUserManager userManager,
        SignInManager<ApplicationUser> signInManager,
        RoleManager<IdentityRole> roleManager,
        IdentityPortabilityHandler identityExporter
        )
    {
        _dbContext = dbContext;
        _userManager = userManager;
        _signInManager = signInManager;
        _roleManager = roleManager;
        _identityExporter = identityExporter;
    }

    [HttpGet("Index")]
    public async Task<IActionResult> Index(string userName = "")
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

        if (user == null) 
            return NotFound();

        _dbContext.Entry(user).State = EntityState.Detached;

        bool isAuditView = false;

        var sessionKey = HttpContext.Session.GetString("UselessInfoHash");
        if (!string.IsNullOrEmpty(sessionKey) && user.Id == User.FindFirstValue(ClaimTypes.NameIdentifier))
        {
            _userManager.DecryptUserDataDirect(user, sessionKey);
        }
        else 
        if (User.IsInRole("Admin"))
        {
            if (_userManager.DecryptUserDataByServer(user))
            {
                isAuditView = true;
            }
            else
            {
                return Forbid(); // Audit notification failed
            }
        }

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
            RoleSelections = roleSelections,
            IsAuditView = isAuditView,
            CanEditRoles = User.IsInRole("Admin")
        };

        if (string.IsNullOrEmpty(userName) || userName == User.Identity.Name)
        {
            model.UserSecurity = user.SecurityPreferences;
        }

        return Request.IsAjaxRequest() ? PartialView(model) : View(model);
    }

    [HttpPost("Update")]
    public async Task<IActionResult> Update(ProfileViewModel model)
    {
        // 1. Fetch Tracked User (Encrypted state from DB)
        var user = await _userManager.FindByIdAsync(model.User.Id);
        if (user == null) 
            return NotFound();

        var currentUserId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        bool isSelfEdit = (user.Id == currentUserId);
        bool isAdmin = User.IsInRole("Admin");

        if (!isSelfEdit && !isAdmin) 
            return Forbid();

        // 2. KEY HYDRATION & DECRYPTION (Critical for editing)
        bool keyLoaded = false;

        if (isSelfEdit)
        {
            var userKey = HttpContext.Session.GetString("UselessInfoHash");
            if (!string.IsNullOrEmpty(userKey))
            {
                // Unlocks user in memory -> IsEncrypted = false
                keyLoaded = _userManager.DecryptUserDataDirect(user, userKey);
            }
        }
        else if (isAdmin)
        {
            // Admin Edit: Load key via Server Master Key logic (silent load for write)
            keyLoaded = _userManager.LoadKeyForAdminAction(user);

            // Manually decrypt to prepare for edit state
            if (keyLoaded && user.IsEncrypted)
            {
                _userManager.DecryptUserDataDirect(user, user.EncryptionKey);
            }
        }

        if (!keyLoaded)
        {
            ModelState.AddModelError("", "Decryption failed. Cannot save changes without valid encryption context.");
            // Re-populate roles for view
            model.CanEditRoles = isAdmin;
            return Request.IsAjaxRequest() ? PartialView("Index", model) : View("Index", model);
        }

        // --- 3. PASSWORD CHANGE LOGIC (Optional) ---
        if (!string.IsNullOrEmpty(model.NewPassword))
        {
            if (isSelfEdit)
            {
                if (string.IsNullOrEmpty(model.CurrentPassword))
                {
                    ModelState.AddModelError("CurrentPassword", "Current password is required to set a new one.");
                    return Request.IsAjaxRequest() ? PartialView("Index", model) : View("Index", model);
                }

                var token = await _userManager.GeneratePasswordResetTokenAsync(user);
                var passResult = await _userManager.ChangePasswordAsync(user, model.CurrentPassword, model.NewPassword);
                if (!passResult.Succeeded)
                {
                    foreach (var error in passResult.Errors) ModelState.AddModelError("", error.Description);
                    return Request.IsAjaxRequest() ? PartialView("Index", model) : View("Index", model);
                }

                // RE-LOCK PHSK with new password hash
                // We use the PHSK that is currently loaded in user.EncryptionKey
                _userManager.ReLockPHSKAsync(user, user.EncryptionKey, model.NewPassword);

                // Refresh cookie
                await _signInManager.RefreshSignInAsync(user);
            }
            else if (isAdmin)
            {
                // Admin reset (Requires generating token usually, but here we force reset)
                var token = await _userManager.GeneratePasswordResetTokenAsync(user);
                var resetResult = await _userManager.ResetPasswordAsync(user, token, model.NewPassword);
                if (!resetResult.Succeeded)
                {
                    foreach (var error in resetResult.Errors) ModelState.AddModelError("", error.Description);
                    return Request.IsAjaxRequest() ? PartialView("Index", model) : View("Index", model);
                }

                // Admin Re-Lock:
                // Since Admin has the PHSK loaded (via Server Key), they can re-lock it with the new password hash.
                _userManager.ReLockPHSKAsync(user, user.EncryptionKey, model.NewPassword);
            }
        }

        // --- 4. APPLY PROFILE EDITS ---
        user.Name = model.User.Name;
        user.Surname = model.User.Surname;
        user.DisplayNickname = model.User.DisplayNickname;

        // Apply new Sensitive Data (Cleartext from form -> Object)
        // UserManager.UpdateAsync will see IsEncrypted=false and re-encrypt this.

        // Security Settings (Self Only)
        if (isSelfEdit && model.UserSecurity != null)
        {
            user.SecurityPreferences = model.UserSecurity;
        }

        // Roles (Admin Only)
        if (isAdmin)
        {
            var currentRoles = await _userManager.GetRolesAsync(user);
            var selectedRoles = model.RoleSelections.Where(x => x.IsSelected).Select(x => x.RoleName).ToList();

            await _userManager.AddToRolesAsync(user, selectedRoles.Except(currentRoles));
            await _userManager.RemoveFromRolesAsync(user, currentRoles.Except(selectedRoles));
        }

        var result = await _userManager.UpdateAsync(user);

        if (result.Succeeded)
        {
            TempData["StatusMessage"] = "Profile updated successfully.";
            return RedirectToAction("Index");
        }

        foreach (var error in result.Errors)
        {
            ModelState.AddModelError("", error.Description);
        }

        return Request.IsAjaxRequest() ? PartialView("Index", model) : View("Index", model);
    }

    [HttpGet("Download")]
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

        // 2. KEY HYDRATION & DECRYPTION (Critical for editing)
        string sessionKey = string.Empty;

        if (isSelfDownload)
        {
            sessionKey = HttpContext.Session.GetString("UselessInfoHash");
        }
        else if (isAdmin)
        {
            if (_userManager.DecryptUserDataByServer(user))
            {
                sessionKey = user.EncryptionKey;
            }
            else
            {
                return Forbid(); // Audit notification failed
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
            var filePath = await _identityExporter.ExportUserDataAsync(user.UserName, sessionKey, tempPath);

            if (string.IsNullOrEmpty(filePath) || !System.IO.File.Exists(filePath)) 
                return NotFound("Export generation failed.");

            var fileBytes = await System.IO.File.ReadAllBytesAsync(filePath);
            var fileName = $"Identity_{user.UserName}_{DateTime.Now:yyyyMMdd}.json";

            return File(fileBytes, "application/json", fileName);
        }
        finally
        {
            if (Directory.Exists(tempPath)) Directory.Delete(tempPath, true);
        }
    }
}