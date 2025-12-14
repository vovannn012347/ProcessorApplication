using System.Net;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace ProcessorApplication.Attributes;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public class SessionKeyRequiredAttribute : ActionFilterAttribute
{
    private readonly string _sessionKey;

    public SessionKeyRequiredAttribute(string sessionKeyName = "UselessInfoHash")
    {
        _sessionKey = sessionKeyName;
    }

    public override void OnActionExecuting(ActionExecutingContext context)
    {
        var httpContext = context.HttpContext;
        var session = httpContext.Session;

        if (!session.IsAvailable 
            || 
            httpContext.User.Identity?.IsAuthenticated != true 
            || 
            string.IsNullOrEmpty(session.GetString(_sessionKey)))
        {
            // 1. Is this an AJAX request? (Check for X-Requested-With header)
            if (httpContext.Request.Headers["X-Requested-With"] == "XMLHttpRequest")
            {
                // For AJAX/Fetch requests, return a specific error code + custom header
                // This prevents silent failure and lets JS handle the redirect.
                context.HttpContext.Response.Headers.Add("X-Session-Expired", "true");
                context.Result = new StatusCodeResult(403);
            }
            else
            {
                var requestPath = httpContext.Request.Path + httpContext.Request.QueryString;
                context.Result = 
                    new RedirectResult("/Main/Account/SessionTimeout?returnUrl=" + Uri.EscapeDataString(requestPath.ToString()));
            }
        }
    }
}