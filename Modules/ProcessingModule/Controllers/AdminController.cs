using System.Text;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

using Newtonsoft.Json;

using ProcessorApplication.Attributes;
using ProcessorApplication.Utils;

using ProcessingModule.Configuration;
using ProcessingModule.Database;
using ProcessingModule.Database.Models;
using ProcessingModule.Infrastructure;
using ProcessingModule.Infrastructure.Util;
using ProcessingModule.Services;
using ProcessingModule.Services.Installation;

namespace ProcessingModule.Controllers;

[ModuleRoute(ProcessorModuleEntryPoint.MODULE_ID)]
[Route("[controller]/[action]/{id?}")]
[Authorize(Policy = "AdminLocalPolicy")]
public class AdminController : Controller
{
    private readonly ProcessorDbContext _db;
    private readonly IScriptIndexer _scriptIndexer;
    private readonly IProcessingService _processingService;
    private readonly IScriptInstaller _scriptInstaller;
    private readonly IOptionsMonitor<ProcessorSettings> _settings;

    public AdminController(
        IProcessingService processingService,
        IScriptIndexer scriptIndexer,
        IScriptInstaller scriptInstaller,
        IOptionsMonitor<ProcessorSettings> settings,
        ProcessorDbContext db)
    {
        _db = db;
        _scriptIndexer = scriptIndexer;
        _processingService = processingService;
        _scriptInstaller = scriptInstaller;
        _settings = settings;
    }



    // Paginated Global Queue for Admins
    public async Task<IActionResult> QueueAdmin(int page = 1)
    {
        int pageSize = 20;
        var totalJobs = await _db.Jobs.CountAsync();

        var jobs = await _db.Jobs
            .OrderByDescending(j => j.CreatedTime)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Include(j => j.SubJobs)
            .ToListAsync();

        ViewBag.CurrentPage = page;
        ViewBag.TotalPages = (int)Math.Ceiling(totalJobs / (double)pageSize);
        ViewBag.IsAdminView = true;


        if (Request.IsAjaxRequest())
        {
            return PartialView(jobs);
        }

        return View(jobs);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> PurgeJob(Guid id)
    {
        var job = await _db.Jobs.Include(j => j.SubJobs).FirstOrDefaultAsync(j => j.Id == id);
        if (job == null) return NotFound();

        try
        {
            // 1. Delete physical directory if it exists
            if (Directory.Exists(job.PhysicalPathRoot))
            {
                Directory.Delete(job.PhysicalPathRoot, true);
            }

            // 2. Remove from database
            _db.Jobs.Remove(job);
            await _db.SaveChangesAsync();

            return Ok();
        }
        catch (Exception)
        {
            return StatusCode(500, "Error purging job data from disk.");
        }
    }


    [HttpGet]
    public IActionResult ReindexLogs()
    {
        if (Request.IsAjaxRequest())
        {
            return PartialView();
        }

        return View();
    }

    [HttpGet]
    public async Task ConsecutiveReindex()
    {
        var response = Response;
        response.Headers.Add("Content-Type", "text/event-stream");
        response.Headers.Add("Cache-Control", "no-cache");
        response.Headers.Add("Connection", "keep-alive");

        // This token is the key. It triggers if the user closes the tab OR the console.
        var cancellationToken = HttpContext.RequestAborted;

        try
        {
            await _scriptIndexer.ContinuousReindexAsync(HttpContext, async (msg) =>
            {
                // Check if user disconnected before sending next chunk
                if (cancellationToken.IsCancellationRequested) return;

                var data = $"data: {DateTime.Now:HH:mm:ss} | {msg}\n\n";
                await response.Body.WriteAsync(Encoding.UTF8.GetBytes(data), cancellationToken);
                await response.Body.FlushAsync(cancellationToken);
            }, cancellationToken);

            if (!cancellationToken.IsCancellationRequested)
            {
                await response.Body.WriteAsync(Encoding.UTF8.GetBytes("data: [DONE]\n\n"), cancellationToken);
                await response.Body.FlushAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // This is expected when the user closes the console/tab
            // We just let the method exit gracefully.
        }
    }

    [HttpGet]
    public async Task<IActionResult> GetSubJobDetails(Guid id)
    {
        var details = await _processingService.GetSubJobDetailsAsync(id);
        if (details == null) return NotFound();

        return PartialView("_SubJobDetails", details);
    }

    // Action to trigger a full file-system scan for scripts


    // Precise reindexing for specific external script additions.
    // folderPath may be relative to ScriptSourcePath or absolute, but must point inside it.
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ReindexPrecise(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath)) return BadRequest();

        string root = Path.GetFullPath(_settings.CurrentValue.ScriptSourcePath);
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(Path.IsPathRooted(folderPath) ? folderPath : Path.Combine(root, folderPath));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return BadRequest(new { status = "error", message = "Invalid folder path." });
        }

        if (!ScriptPathRules.IsInside(root, fullPath) ||
            Path.GetRelativePath(root, fullPath).StartsWith(ScriptInstaller.StagingFolderName, StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest(new { status = "error", message = "The folder must be inside the script source folder." });
        }

        if (!Directory.Exists(fullPath)) return NotFound(new { status = "error", message = "Folder not found." });

        var entry = await _scriptIndexer.PreciseReindexAsync(fullPath, HttpContext, saveDb: true);
        return Ok(new { status = entry != null ? "success" : "skipped", path = Path.GetRelativePath(root, fullPath) });
    }

    /// <summary>
    /// Verifies a user's result hash chain (seals, links, content, head backup).
    /// checkFiles=true also re-hashes the result files still on disk.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> VerifyResultChain(string owner, bool checkFiles = false,
        [FromServices] ProcessingModule.Services.Processing.IResultChainService? chain = null)
    {
        if (string.IsNullOrWhiteSpace(owner) || chain == null) return BadRequest();
        var result = await chain.VerifyAsync(owner, checkFiles, HttpContext.RequestAborted);
        return Content(JsonConvert.SerializeObject(result, Formatting.Indented), "application/json");
    }

    // ------------------------------------------------------------------
    // Script package upload
    // ------------------------------------------------------------------

    [HttpGet]
    public IActionResult InstallScript()
    {
        var s = _settings.CurrentValue;
        ViewBag.MaxUploadMb = s.MaxScriptUploadMb;
        ViewBag.MaxExtractedMb = s.MaxScriptExtractedMb;

        if (Request.IsAjaxRequest())
        {
            return PartialView();
        }

        return View();
    }

    /// <summary>
    /// Receives a .zip as multipart/form-data (field "package"), streamed straight to disk.
    /// The antiforgery token must be sent in the RequestVerificationToken header so the
    /// framework doesn't buffer the whole form to look for it.
    /// Valid packages without a conflict are installed in the same request.
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    [DisableRequestSizeLimit]
    [RequestFormLimits(MultipartBodyLengthLimit = long.MaxValue, ValueLengthLimit = int.MaxValue)]
    public async Task<IActionResult> UploadScriptPackage()
    {
        var ct = HttpContext.RequestAborted;
        long maxUpload = _settings.CurrentValue.MaxScriptUploadMb * 1024L * 1024L;

        // Kestrel limit for this request: the configured size plus room for the multipart headers.
        // The installer still counts the file bytes itself.
        var sizeFeature = HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (sizeFeature is { IsReadOnly: false }) sizeFeature.MaxRequestBodySize = maxUpload + 1024 * 1024;

        if (Request.ContentLength > maxUpload + 1024 * 1024)
            return JsonResult(Rejected($"The file is larger than the {_settings.CurrentValue.MaxScriptUploadMb} MB upload limit."), 413);

        if (string.IsNullOrEmpty(Request.ContentType) ||
            !MediaTypeHeaderValue.TryParse(Request.ContentType, out var mediaType) ||
            !mediaType.MediaType.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase))
            return JsonResult(Rejected("Expected a multipart/form-data upload."), 400);

        string boundary = HeaderUtilities.RemoveQuotes(mediaType.Boundary).Value ?? "";
        if (string.IsNullOrWhiteSpace(boundary)) return JsonResult(Rejected("The upload has no multipart boundary."), 400);

        var reader = new MultipartReader(boundary, Request.Body);
        MultipartSection? section;
        while ((section = await reader.ReadNextSectionAsync(ct)) != null)
        {
            if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition) ||
                !disposition.DispositionType.Equals("form-data") ||
                (string.IsNullOrEmpty(disposition.FileName.Value) && string.IsNullOrEmpty(disposition.FileNameStar.Value)))
            {
                continue; // not a file field
            }

            string fileName = Path.GetFileName(HeaderUtilities.RemoveQuotes(
                string.IsNullOrEmpty(disposition.FileNameStar.Value) ? disposition.FileName : disposition.FileNameStar).Value ?? "package.zip");

            if (!fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                return JsonResult(Rejected("Only .zip packages are supported."), 400);

            var staged = await _scriptInstaller.StageAsync(section.Body, fileName, ct);

            if (staged.Status == ScriptInstallStatus.Ready && staged.StageId != null)
            {
                var installed = await _scriptInstaller.CommitAsync(staged.StageId, overwrite: false, HttpContext, ct);
                if (installed.Status == ScriptInstallStatus.Failed) _scriptInstaller.Discard(staged.StageId);
                return JsonResult(installed);
            }

            return JsonResult(staged);
        }

        return JsonResult(Rejected("No file was found in the upload."), 400);
    }

    /// <summary>Installs a package that was staged earlier (used after "Replace" is confirmed).</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CommitScriptInstall(string stageId, bool overwrite)
    {
        var result = await _scriptInstaller.CommitAsync(stageId, overwrite, HttpContext, HttpContext.RequestAborted);
        return JsonResult(result);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult DiscardScriptUpload(string stageId)
    {
        _scriptInstaller.Discard(stageId);
        return Ok();
    }

    private static ScriptInstallResult Rejected(string message)
    {
        var result = new ScriptInstallResult { Status = ScriptInstallStatus.Rejected, Message = message };
        result.Report.Error("archive", message);
        return result;
    }

    // Serialized with Newtonsoft so the [JsonProperty] snake_case names are used.
    private ContentResult JsonResult(ScriptInstallResult result, int statusCode = 200) => new()
    {
        Content = JsonConvert.SerializeObject(result),
        ContentType = "application/json",
        StatusCode = statusCode
    };
}