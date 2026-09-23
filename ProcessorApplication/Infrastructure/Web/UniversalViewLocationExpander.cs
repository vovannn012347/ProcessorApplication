using Microsoft.AspNetCore.Mvc.Razor;

namespace ProcessorApplication.Infrastructure.Web;
public class UniversalViewLocationExpander : IViewLocationExpander
{
    public void PopulateValues(ViewLocationExpanderContext context)
    {
        // We tag the context if the controller belongs to our Processor module
        // this uses [ModuleRoute("[module_area_name_example]")] to set area
        if (context.ActionContext.RouteData.Values.TryGetValue("area", out var area))
        {
            context.Values["module_area"] = area?.ToString();
        }
    }

    public IEnumerable<string> ExpandViewLocations(ViewLocationExpanderContext context, IEnumerable<string> viewLocations)
    {
        if (context.Values.TryGetValue("module_area", out string areaName) && !string.IsNullOrEmpty(areaName))
        {
            // We provide a unique path for the engine to search.
            // Because our views are compiled into the DLL at "/Views/...",
            // this expansion allows the engine to find them without colliding with the Main app.
            return new[]
            {
                $"/Views/{areaName}/{{1}}/{{0}}.cshtml", // Mapped to: /Views/Providerless/Settings/Index1.cshtml
                $"/Views/{areaName}/Shared/{{0}}.cshtml",
                "/Views/Shared/{0}.cshtml"                // Shared main host layout visibility fix
            };
        }

        return viewLocations;
    }
}