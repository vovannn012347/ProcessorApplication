using System.Net;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

using ProcessorApplication.Configuration.Settings;

using static Microsoft.EntityFrameworkCore.DbLoggerCategory.Database;

namespace ProcessorApplication.Policy;

public class LocalRequirement : IAuthorizationRequirement { }

// 2. Handler: this computer, or an address/network listed in SecuritySettings.AdminNetworks
public class LocalhostHandler : AuthorizationHandler<LocalRequirement>
{
    private readonly IOptionsMonitor<SecuritySettings> _security;

    public LocalhostHandler(IOptionsMonitor<SecuritySettings> security)
    {
        _security = security;
    }

    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, LocalRequirement requirement)
    {
        var connection = (context.Resource as HttpContext)?.Connection;

        if (AdminNetworkRules.IsAllowed(connection?.RemoteIpAddress, connection?.LocalIpAddress, _security.CurrentValue.AdminNetworks))
        {
            context.Succeed(requirement);
        }

        // Otherwise, access is denied by default unless another policy succeeds.
        return Task.CompletedTask;
    }
}
