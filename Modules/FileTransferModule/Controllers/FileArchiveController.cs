using FileSharingModule.Services;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

using ProcessorApplication.Attributes;

namespace FileSharingModule.Controllers;


[ModuleRoute(FileTrModuleEntryPoint.MODULE_ID)]
[Route("/[area]/[controller]/[action]/{id?}")]
[Authorize(Policy = "AdminLocalPolicy", Roles = "Admin")]
[ApiExplorerSettings(IgnoreApi = true)]
public class FileArchiveController : Controller
{
    private readonly IFileArchiveService _archiveService;

    public FileArchiveController(IFileArchiveService archiveService)
    {
        _archiveService = archiveService;
    }

    [HttpGet]
    public async Task<IActionResult> Index()
    {
        var trackedFiles = await _archiveService.GetTrackedFilesAsync(includeHidden: true);

        if (Request.Headers["X-Requested-With"] == "XMLHttpRequest")
        {
            return PartialView("_TrackedFileListPartial", trackedFiles);
        }

        return View(trackedFiles);
    }

    [HttpGet]
    public async Task<IActionResult> GetTrackedList()
    {
        var trackedFiles = await _archiveService.GetTrackedFilesAsync(includeHidden: true);
        return PartialView("_TrackedFileListPartial", trackedFiles);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> IngestFile(IFormFile? uploadFile, bool isHidden = false)
    {
        if (uploadFile == null || uploadFile.Length == 0)
        {
            return Json(new { success = false, message = "No file selected or file is empty." });
        }

        string tempFilePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}_{uploadFile.FileName}");
        try
        {
            using (var stream = new FileStream(tempFilePath, FileMode.Create))
            {
                await uploadFile.CopyToAsync(stream);
            }

            var record = await _archiveService.IngestAndTrackFileAsync(tempFilePath, isHidden);
            return Json(new { success = true, contentHash = record.Manifest.ContentHash, fileName = record.Manifest.FileName });
        }
        catch (Exception ex)
        {
            return Json(new { success = false, message = ex.Message });
        }
        finally
        {
            if (System.IO.File.Exists(tempFilePath))
            {
                try { System.IO.File.Delete(tempFilePath); } catch { }
            }
        }
    }

    [HttpGet]
    public async Task<IActionResult> DownloadManifest(string contentHash)
    {
        if (string.IsNullOrWhiteSpace(contentHash)) return BadRequest("ContentHash is required.");

        var record = await _archiveService.GetTrackedFileAsync(contentHash);
        if (record == null || !System.IO.File.Exists(record.ManifestFilePath))
        {
            return NotFound("Manifest file not found.");
        }

        byte[] fileBytes = await System.IO.File.ReadAllBytesAsync(record.ManifestFilePath);
        return File(fileBytes, "application/json", $"{record.Manifest.FileName}.ptorrent");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ToggleVisibility(string contentHash)
    {
        if (string.IsNullOrWhiteSpace(contentHash)) return BadRequest();

        bool result = await _archiveService.ToggleVisibilityAsync(contentHash);
        return Json(new { success = result });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteFile(string contentHash, bool deletePhysical = false)
    {
        if (string.IsNullOrWhiteSpace(contentHash)) return BadRequest();

        bool result = await _archiveService.DeleteTrackedFileAsync(contentHash, deletePhysical);
        return Json(new { success = result });
    }
}