using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

using ProcessorApplication.Attributes;
using ProcessorApplication.Database;
using ProcessorApplication.Database.Models;
using ProcessorApplication.Models.User;
using ProcessorApplication.Services.User;
using ProcessorApplication.Utils;

namespace ProcessorApplication.Controllers;
[ModuleRoute("Main")]
[Route("[controller]/[action]/{id?}")]
[SessionKeyRequired("UselessInfoHash")]
[Authorize(Policy = "AdminLocalPolicy", Roles = "Admin")]
public class UsersController : Controller
{
    private readonly ProcessorApplicationUserManager _userManager;
    private readonly ILogger<UsersController> _logger;
    private readonly AppDbContext _database;

    public UsersController(
        ProcessorApplicationUserManager userManager,
        ILogger<UsersController> logger,
        AppDbContext database)
    {
        _userManager = userManager;
        _logger = logger;
        _database = database;
    }

    [HttpGet]
    public async Task<IActionResult> Index(int pageNumber = 1, int pageSize = 20)
    {
        if (pageSize <= 0) pageSize = 20;
        if (pageNumber <= 0) pageNumber = 1;

        // --- FIX: Using AppDbContext for direct, high-performance query ---
        var usersQuery = _database.Users.OfType<ApplicationUser>().AsNoTracking();

        var totalCount = await usersQuery.CountAsync();
        var totalPages = (int)Math.Ceiling(totalCount / (double)pageSize);

        var users = await usersQuery
            .OrderBy(u => u.Surname)
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .Select(u => new UserSummaryViewModel
            {
                // Only select non-encrypted, public PII fields
                Id = u.Id,
                UserName = u.UserName,
                Name = u.Name,
                Surname = u.Surname,
                DisplayNickname = u.DisplayNickname,
                IsLockedOut = u.LockoutEnd.HasValue && u.LockoutEnd.Value > DateTimeOffset.Now
            })
            .ToListAsync();

        var viewModel = new PagedUserListViewModel
        {
            PageNumber = pageNumber,
            PageSize = pageSize,
            TotalPages = totalPages,
            TotalCount = totalCount,
            Users = users
        };

        return Request.IsAjaxRequest() ? PartialView("_UserListPartial", viewModel) : View("_UserListPartial", viewModel);
    }


    public class UserActionLockout
    {
        public string UserName { get; set; }
        public bool IsLocked { get; set; }
    }

    [HttpPost]
    public async Task<IActionResult> SetLockout([FromBody] UserActionLockout lockout)
    {
        var user = await _userManager.FindByNameAsync(lockout.UserName);
        if (user == null) return NotFound();

        // The desired lockout end date
        DateTimeOffset? lockUntil;
        string message;

        if (lockout.IsLocked)
        {
            lockUntil = DateTimeOffset.MaxValue;
            await _userManager.SetLockoutEnabledAsync(user, true);
            message = "User locked out indefinitely.";
            _logger.LogWarning("Admin locked out user {UserId}", user.UserName);
        }
        else
        {
            lockUntil = DateTimeOffset.UtcNow;
            message = "User unlocked successfully.";
            _logger.LogInformation("Admin unlocked user {UserId}", user.UserName);
        }

        var result = await _userManager.SetLockoutEndDateAsync(user, lockUntil);

        if (result.Succeeded)
        {
            return Json(new { success = true, message = message });
        }

        // Fallback error if the update failed
        var errors = string.Join(", ", result.Errors.Select(e => e.Description));
        return Json(new { success = false, message = $"Failed to update lockout state: {errors}" });
    }

    public class UserActionDelete
    {
        public string UserName { get; set; }
    }
    [HttpPost]
    public async Task<IActionResult> Delete([FromBody] UserActionDelete delete)
    {
        var user = await _userManager.FindByNameAsync(delete.UserName);
        if (user == null) return NotFound();

        var result = await _userManager.DeleteAsync(user);
        if (!result.Succeeded) return BadRequest(new { success = false, message = "Failed to delete user." });

        _logger.LogWarning("Admin deleted user {UserId}", user.UserName);
        return Json(new { success = true, message = "User deleted." });
    }
}
