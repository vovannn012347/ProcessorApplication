using System.Diagnostics;

using Microsoft.AspNetCore.Mvc;

using ProcessorApplication.Models;
using ProcessorApplication.Services;

namespace ProcessorApplication.Controllers;

public class NavigationController : Controller
{
    private readonly IModuleService _moduleService;

    public NavigationController(IModuleService moduleService)
    {
        _moduleService = moduleService;
    }

    // Action to get the TOP NAVBAR modules
    [HttpGet]
    public IActionResult GetModules()
    {
        var modules = _moduleService.GetModuleInfo();
        return Json(modules);
    }

    // Action to get the SIDEBAR menu
    [HttpGet]
    public IActionResult GetModuleMenu(string moduleId)
    {
        var menuItems = _moduleService.GetMenuItems(moduleId);

        // Return the partial view, passing it the list of menu items
        var view = PartialView("_ModuleMenu", menuItems);

        return view;
    }
}