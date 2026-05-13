using Microsoft.AspNetCore.Mvc.Razor;

namespace ProcessingModule.Infrastructure.Web;
public class ProcessorViewLocationExpander : IViewLocationExpander
{
    public void PopulateValues(ViewLocationExpanderContext context)
    {
        // We tag the context if the controller belongs to our Processor module
        if (context.ActionContext.ActionDescriptor.DisplayName?.Contains(ProcessorModule.MODULE_ID) == true)
        {
            context.Values["module"] = ProcessorModule.MODULE_ID;
        }
    }

    public IEnumerable<string> ExpandViewLocations(ViewLocationExpanderContext context, IEnumerable<string> viewLocations)
    {
        if (context.Values.TryGetValue("module", out string? module) && module == ProcessorModule.MODULE_ID)
        {
            // We provide a unique path for the engine to search.
            // Because our views are compiled into the DLL at "/Views/...",
            // this expansion allows the engine to find them without colliding with the Main app.
            var moduleLocations = new[] {
                    $"/Views/{ProcessorModule.MODULE_ID}/{{1}}/{{0}}.cshtml",
                    "/Views/Processing/Shared/{0}.cshtml",
                    "/Views/Shared/{0}.cshtml" //"_layout.cshtml" visibility fix
                };

            // We return these as the PRIMARY locations to search
            //return moduleLocations;
            return moduleLocations;
        }

        return viewLocations;
    }
}