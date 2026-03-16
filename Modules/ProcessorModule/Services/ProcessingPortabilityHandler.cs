using Common.Infrastructure;
using Common.Interfaces;
using Common.Interfaces.Menu;

using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

using ProcessorApplication.Infrastructure;

using ProcessorModule.Database;
using ProcessorModule.Database.Models;

namespace ProcessorApplication.Services;

public class ProcessingPortabilityHandler : IPortabilityHandler
{
    private readonly ProcessorDbContext _db;

    public string ModuleIdentifier => "Processing";

    public async Task<ModuleExportSummary> GetExportSummaryAsync(string userId)
    {
        int count = _db.Jobs.Where(j => j.InitiatorUserId == userId).Count();
        return new ModuleExportSummary
        {
            DisplayName = "Processed Tasks & Results",
            Description = "Exports full orchestration workspaces, input parameters, and generated clinical artifacts.",
            TotalItemCount = count
        };
    }

    public async Task<PaginatedList<ExportableItem>> GetExportableItemsAsync(string userId, int pageIndex, int pageSize)
    {
        //[cite_start]// Define the base query for this user 
        var query = _db.Jobs
            .AsNoTracking()
            .Where(j => j.InitiatorUserId == userId)
            .OrderByDescending(j => j.CreatedTime);

        // 1. Get the total count once
        var totalCount = await query.CountAsync();

        //[cite_start]// 2. Fetch the specific page and project into the ViewModel 
        var items = await query
            .Skip(pageIndex * pageSize)
            .Take(pageSize)
            .Select(j => new ExportableItem
            {
                Id = j.Id.ToString(),
                DisplayName = $"Task {j.Id.ToString().Substring(0, 8)}",
                Metadata = $"Status: {j.Status} | Created: {j.CreatedTime:yyyy-MM-dd}"
            })
            .ToListAsync();

        return new PaginatedList<ExportableItem>(items, totalCount, pageIndex, pageSize);
    }

    public async Task<List<string>> ProcessExportAsync(
        string userId,
        List<string> selectedItemIds,
        string userKey,
        string destinationFolder)
    {
        IQueryable<OrchestratedTask> jobsQuery = _db.Jobs
            .Where(j => j.InitiatorUserId == userId);

        jobsQuery = jobsQuery.Where(j => selectedItemIds.Contains(j.Id.ToString()));
        //if (isExclusionMode)
        //{
        //    // Export everything UNLESS it is in the list
        //    jobsQuery = jobsQuery.Where(j => !itemIds.Contains(j.Id.ToString()));
        //}
        //else
        //{
        //    // Export ONLY items in the list
        //}

        var jobsToExport = await jobsQuery.ToListAsync();
        var exportedFiles = new List<string>();

        foreach (var job in jobsToExport)
        {
            //[cite_start]
            // Perform physical bundling (e.g., zip task folders) 
            // exportedFiles.Add(await BundleJobData(job, destinationFolder));
        }

        return exportedFiles;
    }

    public async Task<bool> ProcessImportAsync(string userId, string sourceFolder) => true;


}