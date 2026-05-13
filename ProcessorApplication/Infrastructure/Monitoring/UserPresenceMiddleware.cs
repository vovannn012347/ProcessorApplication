namespace Infrastructure.Monitoring;

/// <summary>
/// A Singleton service that holds the "Last Seen" timestamps for all visitors.
/// </summary>
public class UserPresenceMiddleware
{
    private readonly RequestDelegate _next;
    private const string ANON_COOKIE = "X-Visitor-Id";

    public UserPresenceMiddleware(RequestDelegate next)
    {
        _next = next;
    }


    public async Task InvokeAsync(HttpContext context, UserPresenceStore store)
    {
        // 1. FILTER: Ignore internal pings
        // Check path or check for a custom header we'll add to the background service
        if (context.Request.Path.StartsWithSegments("/api/Heartbeat") ||
            context.Request.Headers.ContainsKey("X-Internal-Ping"))
        {
            await _next(context);
            return;
        }

        string visitorId = null;

        if (context.User.Identity?.IsAuthenticated == true)
        {
            visitorId = context.User.Identity.Name;
        }
        else
        {
            if (!context.Request.Cookies.TryGetValue("X-Visitor-Id", out visitorId))
            {
                visitorId = System.Guid.NewGuid().ToString();
                context.Response.Cookies.Append("X-Visitor-Id", visitorId, new CookieOptions
                {
                    HttpOnly = true,
                    Expires = System.DateTimeOffset.UtcNow.AddDays(1)
                });
            }
        }

        if (!string.IsNullOrEmpty(visitorId))
        {
            store.RecordActivity(visitorId);
        }

        await _next(context);
    }
}