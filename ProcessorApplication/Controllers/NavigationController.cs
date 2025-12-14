using System.Security.Claims;

using Common.Interfaces.Menu;

using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

using ProcessorApplication.Database.Models;
using ProcessorApplication.Infrastructure;

namespace ProcessorApplication.Controllers;

[Route("/Navigation")]
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

    [HttpGet("GetModules")]
    public IActionResult GetModules()
    {
        var modules = _moduleService.GetModuleInfo();
        return Json(modules);
    }

    [HttpGet("GetModuleMenu")]
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