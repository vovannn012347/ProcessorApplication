using Microsoft.AspNetCore.Mvc.Razor;

using ProcessorModule.Database.Models;
using ProcessorModule.Models.Views;
using ProcessorModule.Services;
using ProcessorModule.Services.Runtime;

namespace ProcessorModule.Infrastructure;
public interface ISandboxProvider
{
    // returns OS-Monitoring or Docker based on settings/capabilities
    public ISandboxProcessing GetActiveProcessor();
    // returns amoutnt of active processing jobs
    public Task<int> GetActiveJobs(); 
}

public interface ISandboxProcessing
{
    public SandboxType GetSandboxType();
    public Task ExecuteJobAsync(Guid subjobId);
    public Task<int> GetActiveJobs();
    public Task<string> GetJobStatusAsync(Guid jobId);
}

public interface IProcessingService
{
    // Fetches the high-level job list for a user
    Task<List<OrchestratedTask>> GetUserJobsAsync(string userId);

    // Lazy-loads specific results for a sub-job from the physical disk
    Task<SubJobDetailsViewModel?> GetSubJobDetailsAsync(Guid subJobId);
    Task ProcessTaskSequenceAsync(Guid taskId, CancellationToken ct);
    Task CreateJournalAsync(OrchestratedTask task);
    Task<bool> StopJobAsync(Guid taskId);
    Task<bool> PauseJobAsync(Guid taskId);
    Task<bool> ResumeJobAsync(Guid taskId);
    Task<bool> RestartJobAsync(Guid taskId);
}