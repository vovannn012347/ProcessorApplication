
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ProcessorApplication.Sqlite.Models;
using ProcessorApplication.Sqlite;

namespace ProcessorApplication.Controllers;

/*
[ApiController]
[Route("trackers")]
public class TrackerController : ControllerBase
{
    private readonly AppDbContext _context;

    public TrackerController(AppDbContext context)
    {
        _context = context;
    }

    private bool CheckLocalIp()
    {
        var remoteIp = HttpContext.Connection.RemoteIpAddress;
        var localIp = HttpContext.Connection.LocalIpAddress;

        return IpUtils.IsLocalIp(localIp, remoteIp);
    }

    [HttpPost("register")]
    public async Task<IActionResult> Register([FromBody] TrackerRegisterRequest request)
    {
        if (!CheckLocalIp())
            return BadRequest("Invalid address format");

        var ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? request.Address;
        var tracker = await _context.Trackers.FirstOrDefaultAsync(t => t.Address == ip && t.Port == request.Port);
        if (tracker == null)
        {
            tracker = new Tracker
            {
                HashKey = request.HashKey ?? string.Empty,
                Address = ip,
                Port = request.Port,
                FriendlyName = request.FriendlyName ?? $"Tracker{ip}_{request.Port}",
                LastSeen = DateTime.UtcNow
            };
            _context.Trackers.Add(tracker);
        }
        else
        {
            tracker.HashKey = request.HashKey ?? tracker.HashKey;
            tracker.FriendlyName = request.FriendlyName ?? tracker.FriendlyName;
            tracker.LastSeen = DateTime.UtcNow;
        }
        await _context.SaveChangesAsync();
        return Ok();
    }

    [HttpGet("list")]
    public async Task<IActionResult> List()
    {
        if (!CheckLocalIp())
            return BadRequest("Invalid address format");

        var trackers = await _context.Trackers
        .Where(t => t.LastSeen > DateTime.UtcNow.AddHours(-24))
        .Select(t => new { t.HashKey, t.Address, t.Port, t.FriendlyName, t.Metric, t.Load })
        .ToListAsync();

        return Ok(trackers);
    }

    [HttpGet("peers/local")]
    public async Task<IActionResult> LocalPeers()
    {
        if (!CheckLocalIp())
            return BadRequest("Invalid address format");

        var peers = await _context.Peers
        .Where(p => p.LastSeen > DateTime.UtcNow.AddHours(-24))
        .Select(p => new { p.HashKey, p.Address, p.Port, p.FriendlyName, p.Metric, p.Load })
        .ToListAsync();
        return Ok(peers);
    }
}

public class TrackerRegisterRequest
{
    public string? HashKey { get; set; }
    public string Address { get; set; } = string.Empty;
    public int Port { get; set; }
    public string? FriendlyName { get; set; }
}
*/