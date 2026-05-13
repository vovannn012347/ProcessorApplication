using Microsoft.AspNetCore.Mvc.Razor;

namespace ProviderlessModule.Infrastructure.Web;
public class ProviderlessViewLocationExpander : IViewLocationExpander
{
    public void PopulateValues(ViewLocationExpanderContext context)
    {
        // We tag the context if the controller belongs to our Processor module
        if (context.ActionContext.ActionDescriptor.DisplayName?.Contains(ProviderlessModule.MODULE_ID) == true)
        {
            context.Values["module"] = ProviderlessModule.MODULE_ID;
        }
    }

    public IEnumerable<string> ExpandViewLocations(ViewLocationExpanderContext context, IEnumerable<string> viewLocations)
    {
        if (context.Values.TryGetValue("module", out string? module) && module == ProviderlessModule.MODULE_ID)
        {
            // We provide a unique path for the engine to search.
            // Because our views are compiled into the DLL at "/Views/...",
            // this expansion allows the engine to find them without colliding with the Main app.
            var moduleLocations = new[] {
                    $"/Views/{ProviderlessModule.MODULE_ID}/{{1}}/{{0}}.cshtml",
                    "/Views/Providerless/Shared/{0}.cshtml",
                    "/Views/Shared/{0}.cshtml" //"_layout.cshtml" visibility fix
                };

            // We return these as the PRIMARY locations to search
            //return moduleLocations;
            return moduleLocations;
        }

        return viewLocations;
    }
}