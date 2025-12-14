using System.Diagnostics;

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;

using ProcessorApplication.Models;
using ProcessorApplication.Models.View;
using ProcessorApplication.Utils;

namespace ProcessorApplication.Controllers;

[Route("Main/Home")]
public class HomeController : Controller
{
    private readonly ILogger<HomeController> _logger;

    public HomeController(ILogger<HomeController> logger)
    {
        _logger = logger;
    }

    [Route("/")] // Catches the root of the entire site: "/"
    [Route("Dashboard")] // Catches /Home/Dashboard
    public IActionResult Dashboard()
    {
        if (Request.IsAjaxRequest())
        {
            return PartialView();
        }

        return View();
    }


    /*
    public IActionResult Privacy()
    {
        return View();
    }*/

    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error()
    {
        return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
    }
}
