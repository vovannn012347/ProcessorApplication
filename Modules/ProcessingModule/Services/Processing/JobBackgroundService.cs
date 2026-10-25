using System.Collections.Concurrent;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using ProcessingModule.Configuration;
using ProcessingModule.Database;
using ProcessingModule.Database.Models;
using ProcessingModule.Infrastructure;

namespace ProcessingModule.Services.Processing;

/// <summary>
/// Step scheduler. The unit of work is one step (one script run), not a whole job.
///
/// - At most <see cref="ProcessorSettings.MaxConcurrentJobs"/> scripts run at once on this node, across all jobs.
/// - A step starts when a slot is free and every step it depends on has completed.
///   Batch jobs have no dependencies, so their scripts are independent: one failing doesn't stop the others.
/// - In an orchestration a failed step skips only the steps that depend on it (directly or indirectly);
///   independent branches keep running.
/// - Waiting steps are taken oldest job first, then by step order.
/// - Paused jobs start no new steps (running ones finish); stopped jobs are left alone.
///
/// <see cref="ProcessingQueue"/> is used as a wake-up signal: anything written to it triggers a scheduling pass.
/// </summary>
public class JobBackgroundService : BackgroundService
{
    private static readonly string[] ActiveJobStatuses = { TaskStatusKeyword.Pending, TaskStatusKeyword.Running };
    private static readonly string[] FailedStepStatuses =
        { TaskStatusKeyword.Error, TaskStatusKeyword.ServerError, TaskStatusKeyword.Skipped, TaskStatusKeyword.Stopped };

    private readonly IServiceScopeFactory _scopes;
    private readonly ProcessingQueue _queue;
    private readonly IOptionsMonitor<ProcessorSettings> _settings;
    private readonly ILogger<JobBackgroundService> _logger;

    // Steps currently executing in this process
    private readonly ConcurrentDictionary<Guid, Task> _running = new();
    // Scheduling passes never overlap: they read and update job/step states together
    private readonly SemaphoreSlim _passLock = new(1, 1);

    public JobBackgroundService(
        IServiceScopeFactory scopes,
        ProcessingQueue queue,
        IOptionsMonitor<ProcessorSettings> settings,
        ILogger<JobBackgroundService> logger)
    {
        _scopes = scopes;
        _queue = queue;
        _settings = settings;
        _logger = logger;
    }

    /// <summary>Number of scripts executing right now.</summary>
    public int RunningCount => _running.Count;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RecoverAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PassAsync(stoppingToken);

                // Sleep until something happens: a new job, a finished step, pause/resume/restart.
                // The timeout is a safety net in case a signal is missed.
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(30));
                try
                {
                    await _queue.DequeueAsync(timeout.Token);
                }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested) { }

                // Collapse a burst of signals into one pass
                while (_queue.TryDequeue(out _)) { }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Step scheduling pass failed");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ContinueWith(_ => { });
            }
        }

        // Let running scripts finish recording their results before shutdown completes
        await Task.WhenAll(_running.Values.ToArray()).ContinueWith(_ => { });
    }

    /// <summary>
    /// After a restart nothing is executing, so steps left "Running" by the previous process are
    /// put back in the queue. Their jobs are picked up by the normal pass.
    /// </summary>
    private async Task RecoverAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProcessorDbContext>();

        var orphaned = await db.ProcessingJobs
            .Include(s => s.ParentJob)
            .Where(s => s.Status == TaskStatusKeyword.Running)
            .ToListAsync(ct);

        foreach (var step in orphaned)
        {
            step.Status = ActiveJobStatuses.Contains(step.ParentJob.Status) || step.ParentJob.Status == TaskStatusKeyword.Paused
                ? TaskStatusKeyword.Pending
                : TaskStatusKeyword.Stopped;
        }

        if (orphaned.Count > 0)
        {
            await db.SaveChangesAsync(ct);
            _logger.LogInformation("Requeued {Count} step(s) interrupted by the last shutdown", orphaned.Count);
        }
    }

    private async Task PassAsync(CancellationToken ct)
    {
        await _passLock.WaitAsync(ct);
        try
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ProcessorDbContext>();
            var journal = scope.ServiceProvider.GetRequiredService<IProcessingService>();

            var jobs = await db.Jobs
                .Include(j => j.SubJobs)
                .Where(j => ActiveJobStatuses.Contains(j.Status))
                .OrderBy(j => j.CreatedTime)
                .ToListAsync(ct);

            int limit = Math.Max(1, _settings.CurrentValue.MaxConcurrentJobs);
            bool halted = ProcessingService.IsSystemHalted;
            var toStart = new List<OrchestratedTaskSubJob>();
            var jobsStarted = new HashSet<Guid>();
            var jobsFinished = new Dictionary<Guid, string>();

            foreach (var job in jobs)
            {
                var byId = job.SubJobs.ToDictionary(s => s.Id);

                // 1. Skip steps whose dependencies failed (repeat until stable: skips propagate down chains)
                bool changed;
                do
                {
                    changed = false;
                    foreach (var step in job.SubJobs.Where(s => s.Status == TaskStatusKeyword.Pending))
                    {
                        bool missingDep = step.DependsOnIds.Any(id => !byId.ContainsKey(id));
                        var failedDep = step.DependsOnIds
                            .Where(byId.ContainsKey)
                            .Select(id => byId[id])
                            .FirstOrDefault(d => FailedStepStatuses.Contains(d.Status));

                        if (missingDep || failedDep != null)
                        {
                            step.Status = TaskStatusKeyword.Skipped;
                            step.ResultMessage = failedDep != null
                                ? $"Skipped: step {failedDep.Sequence} ({failedDep.ScriptId}) did not complete."
                                : "Skipped: a step it depends on is missing from the job.";
                            step.CompletedTime = DateTime.UtcNow;
                            changed = true;
                        }
                    }
                } while (changed);

                // 2. Pick runnable steps while there are free slots
                if (!halted)
                {
                    foreach (var step in job.SubJobs.OrderBy(s => s.Sequence))
                    {
                        if (_running.Count + toStart.Count >= limit) break;
                        if (step.Status != TaskStatusKeyword.Pending || _running.ContainsKey(step.Id)) continue;

                        bool ready = step.DependsOnIds.All(id => byId.TryGetValue(id, out var d) && d.Status == TaskStatusKeyword.Complete);
                        if (!ready) continue;

                        step.Status = TaskStatusKeyword.Running;
                        jobsStarted.Add(job.Id);
                        toStart.Add(step);
                    }
                }

                // 3. Finalize jobs with nothing left to run
                bool anyLeft = job.SubJobs.Any(s =>
                    s.Status == TaskStatusKeyword.Pending ||
                    s.Status == TaskStatusKeyword.Running ||
                    _running.ContainsKey(s.Id));

                if (!anyLeft)
                {
                    jobsFinished[job.Id] = FinalStatus(job.SubJobs);
                }
                else if (halted && job.SubJobs.Any(st => st.Status == TaskStatusKeyword.ServerError) &&
                         !job.SubJobs.Any(st => _running.ContainsKey(st.Id)))
                {
                    // The executor is missing: nothing can run until an admin fixes it and restarts the job
                    jobsFinished[job.Id] = TaskStatusKeyword.ServerError;
                }
            }

            await db.SaveChangesAsync(ct);

            // Job status is changed with conditional updates, so a Pause/Stop clicked while this pass
            // was running is never overwritten.
            foreach (var jobId in jobsStarted)
            {
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE Jobs SET Status = {TaskStatusKeyword.Running} WHERE Id = {jobId} AND Status = {TaskStatusKeyword.Pending}", ct);
            }
            foreach (var (jobId, status) in jobsFinished)
            {
                int updated = await db.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE Jobs SET Status = {status}, CompletedTime = {DateTime.UtcNow} WHERE Id = {jobId} AND (Status = {TaskStatusKeyword.Pending} OR Status = {TaskStatusKeyword.Running})", ct);
                if (updated > 0) journal.DeleteJournal(jobId);
            }

            foreach (var step in toStart)
            {
                Guid id = step.Id;
                _running[id] = Task.Run(() => RunStepAsync(id, ct));
            }
        }
        finally
        {
            _passLock.Release();
        }
    }

    private async Task RunStepAsync(Guid stepId, CancellationToken ct)
    {
        try
        {
            using var scope = _scopes.CreateScope();
            var service = scope.ServiceProvider.GetRequiredService<IProcessingService>();
            await service.ExecuteStepAsync(stepId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Step {StepId} crashed outside the executor", stepId);
            await MarkStepFailedAsync(stepId, ex.Message);
        }
        finally
        {
            _running.TryRemove(stepId, out _);
            await _queue.EnqueueAsync(Guid.Empty); // wake the scheduler: a slot is free
        }
    }

    private async Task MarkStepFailedAsync(Guid stepId, string message)
    {
        try
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ProcessorDbContext>();
            var step = await db.ProcessingJobs.FirstOrDefaultAsync(s => s.Id == stepId);
            if (step != null && step.Status == TaskStatusKeyword.Running)
            {
                step.Status = TaskStatusKeyword.Error;
                step.ResultMessage = message;
                step.CompletedTime = DateTime.UtcNow;
                await db.SaveChangesAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not record failure of step {StepId}", stepId);
        }
    }

    /// <summary>
    /// All steps complete: Complete. Executor missing: ServerError. Some complete: Warning (partial results).
    /// None complete: Error.
    /// </summary>
    public static string FinalStatus(IEnumerable<OrchestratedTaskSubJob> steps)
    {
        var list = steps.ToList();
        if (list.Count == 0) return TaskStatusKeyword.Error;
        if (list.All(s => s.Status == TaskStatusKeyword.Complete)) return TaskStatusKeyword.Complete;
        if (list.Any(s => s.Status == TaskStatusKeyword.ServerError)) return TaskStatusKeyword.ServerError;
        if (list.Any(s => s.Status == TaskStatusKeyword.Complete)) return TaskStatusKeyword.Warning;
        return TaskStatusKeyword.Error;
    }
}
