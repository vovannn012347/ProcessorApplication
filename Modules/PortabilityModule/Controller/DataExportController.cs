using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using PortabilityModule.Services;

using ProcessorApplication.Database.Models;

namespace PortabilityModule.Controllers;

[Authorize]
[Route("DataExport")]
public class DataExportController : Controller
{
    private readonly IDataPortabilityService _portabilityService;
    private readonly UserManager<ApplicationUser> _userManager;

    public DataExportController(
        IDataPortabilityService portabilityService,
        UserManager<ApplicationUser> userManager)
    {
        _portabilityService = portabilityService;
        _userManager = userManager;
    }

    [HttpGet("Index")]
    public IActionResult Index()
    {
        return View();
    }

    [HttpPost("DownloadFullArchive")]
    public async Task<IActionResult> DownloadFullArchive()
    {
        var user = await _userManager.GetUserAsync(User);

        // Generate the ZIP
        var path = await _portabilityService.GenerateFullExportPackageAsync(user);

        var fileBytes = await System.IO.File.ReadAllBytesAsync(path);
        var fileName = $"FullExport_{user.UserName}_{DateTime.Now:yyyyMMdd}.zip";

        // Cleanup the zip file after reading into memory (or use a FileStreamResult with delete-on-close)
        System.IO.File.Delete(path);

        return File(fileBytes, "application/zip", fileName);
    }

    // Import Action (Conceptual - requires file upload UI)
    [HttpPost("ImportData")]
    public async Task<IActionResult> ImportData(IFormFile archive)
    {
        if (archive == null) return BadRequest("No file provided");

        var user = await _userManager.GetUserAsync(User);

        // Save upload to temp
        var tempFile = Path.GetTempFileName();
        using (var stream = System.IO.File.Create(tempFile))
        {
            await archive.CopyToAsync(stream);
        }

        await _portabilityService.ImportFullPackageAsync(user, tempFile);

        System.IO.File.Delete(tempFile);

        return RedirectToAction("Index", new { message = "Import Complete" });
    }
}