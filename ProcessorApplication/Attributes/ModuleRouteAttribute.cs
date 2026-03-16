using System.Net;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationModels;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Routing;

namespace ProcessorApplication.Attributes;

[AttributeUsage(AttributeTargets.Class)]
public class ModuleRouteAttribute : Attribute
{
    public string RouteValue { get; }
    public ModuleRouteAttribute(string moduleId) => RouteValue = moduleId;
}

public class ModuleRoutingConvention : IControllerModelConvention
{
    public void Apply(ControllerModel controller)
    {
        var moduleAttribute = controller.Attributes.OfType<ModuleRouteAttribute>().FirstOrDefault();
        if (moduleAttribute == null) return;

        var prefix = moduleAttribute.RouteValue;

        // Prefix Controller-level routes
        foreach (var selector in controller.Selectors)
        {
            selector.AttributeRouteModel = CreatePrefixedRoute(prefix, selector.AttributeRouteModel);
        }

        // Handle "Absolute" routes on actions (e.g., [Route("/Dashboard")])
        // These ignore the controller prefix, so we must manually add the prefix to them.
        foreach (var action in controller.Actions)
        {
            foreach (var selector in action.Selectors)
            {
                var template = selector.AttributeRouteModel?.Template;
                if (template != null && (template.StartsWith("/") || template.StartsWith("~/")))
                {
                    // Remove leading slash, then combine with prefix
                    var cleanTemplate = template.TrimStart('/', '~');
                    selector.AttributeRouteModel.Template = AttributeRouteModel.CombineTemplates(prefix, cleanTemplate);
                }
            }
        }
    }

    private AttributeRouteModel CreatePrefixedRoute(string prefix, AttributeRouteModel? existing)
    {
        return new AttributeRouteModel
        {
            Template = AttributeRouteModel.CombineTemplates(prefix, existing?.Template ?? "[controller]/[action]")
        };
    }
}