using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;

using System.Linq;

namespace ProcessorApplication.Controllers;

[Route("/Debug/[action]")]
public class DebugController : Controller
{
    private readonly IActionDescriptorCollectionProvider _actionDescriptorCollectionProvider;

    public DebugController(IActionDescriptorCollectionProvider actionDescriptorCollectionProvider)
    {
        _actionDescriptorCollectionProvider = actionDescriptorCollectionProvider;
    }

    public IActionResult Routes()
    {
        var routes = _actionDescriptorCollectionProvider.ActionDescriptors.Items.Select(x => new
        {
            Action = x.RouteValues["Action"],
            Controller = x.RouteValues["Controller"],
            Name = x.AttributeRouteInfo?.Name,
            Template = x.AttributeRouteInfo?.Template,
            Constraint = x.ActionConstraints?.ToString()
        }).OrderBy(r => r.Template).ToList();

        return Json(routes);
    }
}