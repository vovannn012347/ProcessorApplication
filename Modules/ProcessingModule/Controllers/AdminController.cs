using System.Text;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

using ProcessorApplication.Attributes;
using ProcessorApplication.Utils;

using ProcessingModule.Database;
using ProcessingModule.Database.Models;
using ProcessingModule.Infrastructure;
using ProcessingModule.Services;

namespace ProcessingModule.Controllers;

[ModuleRoute(ProcessorModule.MODULE_ID)]
[Route("[controller]/[action]/{id?}")]
[Authorize(Policy = "AdminLocalPolicy")]
public class AdminController : Controller
{
    private readonly ProcessorDbContext _db;
    private readonly IScriptIndexer _scriptIndexer;
    private readonly IProcessingService _processingService;

    public AdminController(
        IProcessingService processingService,
        IScriptIndexer scriptIndexer,
        ProcessorDbContext db)
    {
        _db = db;
        _scriptIndexer = scriptIndexer;
        _processingService = processingService;
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


    // Precise reindexing for specific external script additions
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ReindexPrecise(string folderPath)
    {
        if (string.IsNullOrEmpty(folderPath)) return BadRequest();

        await _scriptIndexer.PreciseReindexAsync(folderPath, HttpContext);
        return Ok(new { status = "success", path = folderPath });
    }
}