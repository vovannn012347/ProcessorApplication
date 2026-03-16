using Microsoft.AspNetCore.Mvc.Razor;

namespace ProcessorModule.Infrastructure.Web;
public class ProcessorViewLocationExpander : IViewLocationExpander
{
    public void PopulateValues(ViewLocationExpanderContext context)
    {
        // We tag the context if the controller belongs to our Processor module
        if (context.ActionContext.ActionDescriptor.DisplayName?.Contains("ProcessorModule") == true)
        {
            context.Values["module"] = "Processor";
        }
    }

    public IEnumerable<string> ExpandViewLocations(ViewLocationExpanderContext context, IEnumerable<string> viewLocations)
    {
        if (context.Values.TryGetValue("module", out string? module) && module == "Processor")
        {
            // We provide a unique path for the engine to search.
            // Because our views are compiled into the DLL at "/Views/...",
            // this expansion allows the engine to find them without colliding with the Main app.
            var moduleLocations = new[] {
                    "/Views/ProcessorModule/{1}/{0}.cshtml",
                    "/Views/ProcessorModule/Shared/{0}.cshtml",
                    "/Views/Shared/{0}.cshtml" //"_layout.cshtml" visibility fix
                };

            // We return these as the PRIMARY locations to search
            //return moduleLocations;
            return moduleLocations;
        }

        return viewLocations;
    }
}