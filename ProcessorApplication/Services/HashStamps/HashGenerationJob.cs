using Microsoft.Extensions.Options;

using ProcessorApplication.Configuration.Settings;
using ProcessorApplication.Infrastructure;

namespace ProcessorApplication.Services.HashStamps;
public class HashGenerationJob : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<HashGenerationJob> _logger;
    private readonly IOptionsMonitor<SecuritySettings> _securitySettings;

    public HashGenerationJob(
        IServiceScopeFactory scopeFactory, 
        ILogger<HashGenerationJob> logger,
        IOptionsMonitor<SecuritySettings> securitySettings)
    {
        _securitySettings = securitySettings;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Hash Generation Background Job is starting.");

        //generate nitial timestamp

        //todo: tie to midnight
        DateTime lastStampTime = DateTime.UtcNow;
        using (var scope = _scopeFactory.CreateScope())
        {
            var hashStampService = scope.ServiceProvider.GetRequiredService<IHashStampService>();

            var stamp = await hashStampService.GetLatestHashAsync();

            if(stamp == null || stamp.StampTime.AddHours(_securitySettings.CurrentValue.HashStampGenerationPeriod) < DateTime.UtcNow)
            {
                stamp = await hashStampService.GenerateAndSaveNewStampAsync();
                var hashBackupService = scope.ServiceProvider.GetRequiredService<IHashBackupService>();
                hashBackupService.BackupServerBlock(stamp);
                lastStampTime = stamp.StampTime;
            }
        } 


        //timely stamp generation job
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                DateTime nextRun = lastStampTime.Date.AddHours(_securitySettings.CurrentValue.HashStampGenerationPeriod); // Next midnight UTC
                TimeSpan delay = nextRun - lastStampTime;

                _logger.LogInformation("Next HashStamp rotation scheduled for {NextRun} UTC. Delaying for {DelayTime}.", nextRun, delay);

                await Task.Delay(delay, stoppingToken);

                using (var scope = _scopeFactory.CreateScope())
                {
                    var hashStampService = scope.ServiceProvider.GetRequiredService<IHashStampService>();

                    var stamp = await hashStampService.GenerateAndSaveNewStampAsync();

                    var hashBackupService = scope.ServiceProvider.GetRequiredService<IHashBackupService>();

                    hashBackupService.BackupServerBlock(stamp);
                }
            }
            catch (TaskCanceledException)
            {
                // The service is stopping.
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error occurred executing Hash Stamp Generation Job.");
                await Task.Delay(TimeSpan.FromHours(0.5), stoppingToken);
            }
        }

        _logger.LogInformation("Hash Generation Background Job is stopping.");
    }
}