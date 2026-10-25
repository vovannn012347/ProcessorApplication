using FileTransferModule.Models;
using FileTransferModule.Models.Torrent;
using FileTransferModule.Services;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

using ProcessorApplication.Attributes;

namespace FileTransferModule.Controllers;


[ModuleRoute(FileTransferModuleEntryPoint.MODULE_ID)]
[Route("/[area]/[controller]/[action]/{id?}")]
[Authorize(Policy = "AdminLocalPolicy", Roles = "Admin")]
[ApiExplorerSettings(IgnoreApi = true)]
public class FileArchiveController : Controller
{
    private readonly IFileArchiveService _archiveService;
    private readonly ISwarmDownloadCoordinator _downloadCoordinator;

    public FileArchiveController(IFileArchiveService archiveService, ISwarmDownloadCoordinator downloadCoordinator)
    {
        _archiveService = archiveService;
        _downloadCoordinator = downloadCoordinator;
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

        // The temp copy keeps the original name (it becomes the manifest's FileName) and is copied into the
        // repository before being tracked: tracking it in place would seed a file deleted a moment later.
        string fileName = Path.GetFileName(uploadFile.FileName);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return Json(new { success = false, message = "The uploaded file has no name." });
        }

        string tempDir = Path.Combine(Path.GetTempPath(), $"ftt_upload_{Guid.NewGuid():N}");
        string tempFilePath = Path.Combine(tempDir, fileName);
        try
        {
            Directory.CreateDirectory(tempDir);
            using (var stream = new FileStream(tempFilePath, FileMode.Create))
            {
                await uploadFile.CopyToAsync(stream);
            }

            var record = await _archiveService.IngestAndTrackFileAsync(tempFilePath, isHidden, copyToRepository: true);
            return Json(new { success = true, contentHash = record.Manifest.ContentHash, fileName = record.Manifest.FileName });
        }
        catch (Exception ex)
        {
            return Json(new { success = false, message = ex.Message });
        }
        finally
        {
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); } catch { }
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

    /// <summary>
    /// Tracks a file already sitting on this server's filesystem (this admin panel is
    /// localhost-only, so a server-side path is a normal thing to hand it - no browser upload
    /// needed). copyToRepository=true copies it into StorageRootPath/repository first and tracks
    /// the copy, leaving the original untouched; false tracks it in place at sourcePath.
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> TrackExistingFile(string sourcePath, bool copyToRepository = false, bool isHidden = false)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            return Json(new { success = false, message = "Source path is required." });
        }

        try
        {
            var record = await _archiveService.IngestAndTrackFileAsync(sourcePath, isHidden, copyToRepository);
            return Json(new { success = true, contentHash = record.Manifest.ContentHash, fileName = record.Manifest.FileName, trackedPath = record.LocalFilePath });
        }
        catch (Exception ex)
        {
            return Json(new { success = false, message = ex.Message });
        }
    }

    /// <summary>
    /// Ingests a .ptorrent manifest someone gave you out-of-band (not resolved via a live node
    /// query) and immediately starts a download task from it.
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> IngestPtorrentAndDownload(IFormFile? ptorrentFile, string? customDirectory = null, string? customFileName = null)
    {
        if (ptorrentFile == null || ptorrentFile.Length == 0)
        {
            return Json(new { success = false, message = "No .ptorrent file provided." });
        }

        try
        {
            string json;
            using (var reader = new StreamReader(ptorrentFile.OpenReadStream()))
            {
                json = await reader.ReadToEndAsync();
            }

            var manifest = PTorrentManifest.DeserializeFromJson(json);
            if (manifest == null || string.IsNullOrEmpty(manifest.ContentHash))
            {
                return Json(new { success = false, message = "Could not parse .ptorrent file - invalid or corrupt manifest." });
            }

            bool isLocalAdmin = User.IsInRole("Admin");
            string taskId = await _downloadCoordinator.StartDownloadAsync(manifest, customDirectory, customFileName, isLocalAdmin);

            return Json(new { success = true, taskId, fileName = manifest.FileName, contentHash = manifest.ContentHash });
        }
        catch (Exception ex)
        {
            return Json(new { success = false, message = ex.Message });
        }
    }

    /// <summary>
    /// Casual per-record settings: visibility (catalog/announce listing), download availability
    /// (whether this node actively serves slice requests for it right now), and the
    /// description/custom fields returned to a peer that explicitly asks about this file
    /// (CatalogGetFileInfo). Only the parameters you pass are changed. Toggling allowDownload
    /// actually starts/stops seeding, not just a flag. customFieldsJson, if given, must be a JSON
    /// object of string to string and replaces the whole set (not a merge).
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateSettings(string contentHash, bool? isHidden = null, bool? allowDownload = null, string? description = null, string? customFieldsJson = null)
    {
        if (string.IsNullOrWhiteSpace(contentHash)) return BadRequest();

        Dictionary<string, string>? customFields = null;
        if (!string.IsNullOrEmpty(customFieldsJson))
        {
            try
            {
                customFields = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(customFieldsJson);
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = $"customFieldsJson is not a valid JSON object of string to string: {ex.Message}" });
            }
        }

        bool result = await _archiveService.UpdateRecordSettingsAsync(contentHash, isHidden, allowDownload, description, customFields);
        return Json(new { success = result });
    }
}