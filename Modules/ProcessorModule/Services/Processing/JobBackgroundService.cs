using System.Threading.Channels;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

using ProcessorModule.Configuration;
using ProcessorModule.Infrastructure;

namespace ProcessorModule.Services.Processing;

public class JobBackgroundService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ProcessingQueue _queue;
    private readonly TaskControlMonitor _monitor;
    private readonly IOptionsMonitor<ProcessorSettings> _settings;
    public JobBackgroundService(
        IServiceProvider sp,
        ProcessingQueue queue,
        TaskControlMonitor monitor, 
        IOptionsMonitor<ProcessorSettings> settings)
    {
        _serviceProvider = sp;
        _queue = queue;
        _monitor = monitor;
        _settings = settings;
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RecoverPendingJobsAsync(); // Step-2 Fallback

        int maxParallel = Math.Max(1, _settings.CurrentValue.MaxConcurrentJobs);
        var workers = Enumerable.Range(0, maxParallel)
            .Select(_ => Task.Run(() => WorkerLoop(stoppingToken), stoppingToken));

        await Task.WhenAll(workers);
    }

    private async Task WorkerLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var taskId = await _queue.DequeueAsync(ct);
                using var scope = _serviceProvider.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<IProcessingService>();

                // Pass the cancellation token from the monitor to support live Stop actions
                var taskToken = _monitor.GetToken(taskId);
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, taskToken);

                await service.ProcessTaskSequenceAsync(taskId, linkedCts.Token);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception) { /* Log error */ }
        }
    }
    private async Task RecoverPendingJobsAsync()
    {
        // Point to the root journals directory
        var journalDir = Path.Combine(_settings.CurrentValue.ResultsOutputPath, MicsConstants.JournalsDirectory);

        if (!Directory.Exists(journalDir)) return;

        // Scan for all orchestration failsafes that weren't deleted on completion
        foreach (var file in Directory.GetFiles(journalDir, $"*.{FileExtensions.Journal}"))
        {
            if (Guid.TryParse(Path.GetFileNameWithoutExtension(file), out var id))
            {
                await _queue.EnqueueAsync(id);
            }
        }
    }
}