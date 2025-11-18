using System.Diagnostics;

using ProcessorApplication.Sqlite;
using ProcessorApplication.Sqlite.Models;

using Microsoft.AspNetCore.Mvc;

using ProcessorApplication.Models;
using ProcessorApplication.Services;

namespace ProcessorApplication.Controllers;
/*
public class SettingsController : Controller
{

    private readonly AppDbContext _dbContext;
    private readonly ILogger<SettingsController> _logger;
    private readonly SqliteConfigurationProvider _provider;

    public SettingsController(AppDbContext dbContext, SqliteConfigurationProvider provider, ILogger<SettingsController> logger)
    {
        _dbContext = dbContext;
        _provider = provider;
        _logger = logger;
    }

    [HttpPost("update-setting")]
    public IActionResult UpdateSetting(string key, string value)
    {
        var existing = _dbContext.Settings.Find(key);
        if (existing == null)
        {
            _dbContext.Settings.Add(new Setting { Key = key, Value = value });
        }
        else
        {
            existing.Value = value;
        }

        _dbContext.SaveChanges();
        _provider.TriggerReload();

        return Ok();
    }

    public IActionResult Index()
    {
        return View();
    }

    public IActionResult Privacy()
    {
        return View();
    }

    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error()
    {
        return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
    }
}

*/