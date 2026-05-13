using System.Security.Claims;

using Common.Interfaces.Menu;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

using ProcessorApplication.Attributes;
using ProcessorApplication.Database.Models;
using ProcessorApplication.Infrastructure;

namespace ProcessorApplication.Controllers;

[AllowAnonymous]
[Route("/Navigation/[action]/{id?}")]
public class NavigationController : Controller
{
    private readonly IServiceProvider _services;
    private readonly IModuleService _moduleService;
    private ClaimsPrincipal CurrentUser => User;

    public NavigationController(
        IServiceProvider services,
        IModuleService moduleService)
    {
        _services = services;
        _moduleService = moduleService;
    }

    [HttpGet]
    public IActionResult GetModules()
    {
        var userRoles = User.FindAll(ClaimTypes.Role).Select(c => c.Value).ToArray();

        var modules =
            _moduleService.GetModuleInfo()
            .Where(r => r.Roles.Length == 0 || r.Roles.Any(requiredRole => userRoles.Contains(requiredRole)))
        .ToList();

        return Json(modules);
    }

    [HttpGet]
    public IActionResult GetModuleMenu(string moduleId)
    {
        var allMenuItems = _moduleService.GetMenuItems(moduleId, _services);

        var filteredItems = allMenuItems.Where(i => string.IsNullOrEmpty(i.Roles)).ToList();

        if (!CurrentUser.Identity.IsAuthenticated)
        {
            return PartialView("_ModuleMenu", filteredItems);
        }

        foreach (var item in allMenuItems.Where(i => !string.IsNullOrEmpty(i.Roles)).ToList())
        {
            var requiredRoles = item.Roles.Split(',')
                .Select(r => r.Trim())
                .ToArray();

            // NOTE: use User.IsInRole for simplicity, but if the role claim 
            // is not loaded - might need to use _userManager.IsInRoleAsync(...) 
            // which requires getting the IdentityUser first.
            if (requiredRoles.Any(role => CurrentUser.IsInRole(role)))
            {
                filteredItems.Add(item);
            }
        }

        return PartialView("_ModuleMenu", filteredItems);
    }
}