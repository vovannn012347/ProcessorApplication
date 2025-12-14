using System.Net;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace ProcessorApplication.Attributes;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public class LocalhostOnlyAttribute : AuthorizeAttribute, IAsyncAuthorizationFilter
{
    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var httpContext = context.HttpContext;
        var connection = httpContext.Connection;

        bool isLocal = false;

        if (connection.RemoteIpAddress != null)
        {
            if (IPAddress.IsLoopback(connection.RemoteIpAddress))
            {
                isLocal = true;
            }
        }

        if (!isLocal && connection.LocalIpAddress != null)
        {
            if (connection.RemoteIpAddress.Equals(connection.LocalIpAddress))
            {
                isLocal = true;
            }
        }

        if (!isLocal)
        {
            context.Result = new StatusCodeResult(404);
            return;
        }

        await Task.CompletedTask;
    }
}