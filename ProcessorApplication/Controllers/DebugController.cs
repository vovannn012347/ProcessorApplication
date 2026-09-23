using System.Linq;
using System.Text;

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Mvc.Razor;
using Microsoft.AspNetCore.Mvc.Razor.RuntimeCompilation;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;

using ProcessorApplication.Attributes;

namespace ProcessorApplication.Controllers;

//[ModuleRoute("Main")]
//[Route("/[area]/[controller]/[action]/{id?}")]
public class DiagnosticController : Controller
{
    private readonly IRazorViewEngine _viewEngine;
    private readonly IOptions<MvcRazorRuntimeCompilationOptions> _runtimeCompilationOptions;

    public DiagnosticController(
        IRazorViewEngine viewEngine,
        IOptions<MvcRazorRuntimeCompilationOptions> runtimeCompilationOptions)
    {
        _viewEngine = viewEngine;
        _runtimeCompilationOptions = runtimeCompilationOptions;
    }

    [Route("Debug/Views")]
    public IActionResult InspectViews(string area = "", string controller1 ="", string view="")
    {
        var sb = new StringBuilder();
        sb.AppendLine("==========================================================");
        sb.AppendLine("[DYNAMIC RAZOR DISK PATH DIAGNOSTIC LOG]");
        sb.AppendLine("==========================================================");

        // 1. CHK INBOUND RENDERING PARAMETERS
        // Let's force an interrogation on the exact tokens Razor calculates
        string targetViewName = string.IsNullOrEmpty(view) ? "Index" : view;
        string targetControllerName = string.IsNullOrEmpty(controller1) ? "Home" : controller1;
        string targetAreaName = string.IsNullOrEmpty(area) ? "Providerless" : area;

        sb.AppendLine($"\nSimulating view resolution with context parameters:");
        sb.AppendLine($"   Action/View: {targetViewName}");
        sb.AppendLine($"   Controller:  {targetControllerName}");
        sb.AppendLine($"   Area/Module: {targetAreaName}");

        // Setup route values manually to isolate compilation behaviors
        ControllerContext.RouteData.Values["area"] = targetAreaName;
        ControllerContext.RouteData.Values["controller"] = targetControllerName;
        ControllerContext.RouteData.Values["action"] = targetViewName;

        var viewEngineResult = _viewEngine.FindView(ControllerContext, targetViewName, isMainPage: true);

        if (viewEngineResult.Success)
        {
            sb.AppendLine($"\n[SUCCESS] View found! Physical layout bound to memory path: {viewEngineResult.View.Path}");
        }
        else
        {
            sb.AppendLine($"\n[CRITICAL FAILURE] Razor could not find '{targetViewName}'.");
            sb.AppendLine("Searched locations checked by the engine:");
            foreach (var location in viewEngineResult.SearchedLocations)
            {
                sb.AppendLine($"   -> {location}");
            }
        }

        // 2. ROOT DISK SCAN USING THE TRUE FILE PROVIDER ARRAY
        sb.AppendLine("\n--- Active MvcRazorRuntimeCompilation File Providers Tree ---");

        var providersChain = _runtimeCompilationOptions.Value.FileProviders;
        sb.AppendLine($"Total registered providers in chain: {providersChain.Count}");

        foreach (var provider in providersChain)
        {
            sb.AppendLine($"Provider Strategy Hook: {provider.GetType().Name}");
            if (provider is PhysicalFileProvider physical)
            {
                sb.AppendLine($"   Anchored Directory Directory: {physical.Root}");

                // Inspect if the Views/ directory physically exists under this specific provider context
                DumpDirectoryTree(physical, "Views", sb, "   ");
            }
        }

        sb.AppendLine("==========================================================");
        return Content(sb.ToString(), "text/plain", Encoding.UTF8);
    }

    private void DumpDirectoryTree(IFileProvider provider, string path, StringBuilder sb, string indent)
    {
        var contents = provider.GetDirectoryContents(path);
        if (!contents.Exists)
        {
            sb.AppendLine($"{indent}[Empty or Folder Not Found]: /{path}");
            return;
        }

        foreach (var item in contents)
        {
            if (item.IsDirectory)
            {
                sb.AppendLine($"{indent} Directory: /{path}/{item.Name}");
                DumpDirectoryTree(provider, Path.Combine(path, item.Name), sb, indent + "   ");
            }
            else
            {
                sb.AppendLine($"{indent} File file: {item.Name} ({item.Length} bytes)");
            }
        }
    }
}
//[Route("/Debug/[action]")]
//public class DebugController : Controller
//{
//    private readonly IActionDescriptorCollectionProvider _actionDescriptorCollectionProvider;

//    public DebugController(IActionDescriptorCollectionProvider actionDescriptorCollectionProvider)
//    {
//        _actionDescriptorCollectionProvider = actionDescriptorCollectionProvider;
//    }

//    public IActionResult Routes()
//    {
//        var routes = _actionDescriptorCollectionProvider.ActionDescriptors.Items.Select(x => new
//        {
//            Action = x.RouteValues["Action"],
//            Controller = x.RouteValues["Controller"],
//            Name = x.AttributeRouteInfo?.Name,
//            Template = x.AttributeRouteInfo?.Template,
//            Constraint = x.ActionConstraints?.ToString()
//        }).OrderBy(r => r.Template).ToList();

//        return Json(routes);
//    }
//}