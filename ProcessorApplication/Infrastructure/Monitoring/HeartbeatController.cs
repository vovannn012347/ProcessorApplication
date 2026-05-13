using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Infrastructure.Monitoring;

[AllowAnonymous]
[Route("api/[controller]")]
public class HeartbeatController : ControllerBase
{
    /// <summary>
    /// Simple 200 OK endpoint for internal reachability pings.
    /// </summary>
    [HttpGet]
    [HttpHead]
    public IActionResult Index() => Ok();
}