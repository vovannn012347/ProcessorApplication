using System.Net;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Authorization;

using static Microsoft.EntityFrameworkCore.DbLoggerCategory.Database;

namespace ProcessorApplication.Policy;

public class LocalhostRequirement : IAuthorizationRequirement { }

// 2. Handler: Checks the IP address
public class LocalhostHandler : AuthorizationHandler<LocalhostRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, LocalhostRequirement requirement)
    {
        var connection = (context.Resource as HttpContext)?.Connection;

        if (connection?.RemoteIpAddress != null && IPAddress.IsLoopback(connection.RemoteIpAddress))
        {
            // If it's a loopback address, authorization succeeds.
            context.Succeed(requirement);
        }

        // Otherwise, access is denied by default unless another policy succeeds.
        return Task.CompletedTask;
    }
}
