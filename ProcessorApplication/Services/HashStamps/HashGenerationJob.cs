using Microsoft.Extensions.Options;

using ProcessorApplication.Configuration.Settings;
using ProcessorApplication.Infrastructure;

namespace ProcessorApplication.Services.HashStamps;

/// <summary>
/// Startup: makes sure the node root key exists, encrypts any old plaintext server keys and rewrites the backups.
/// Then rotates the server key every <see cref="SecuritySettings.HashStampGenerationPeriod"/> hours.
/// </summary>
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

        using (var scope = _scopeFactory.CreateScope())
        {
            var hashStampService = scope.ServiceProvider.GetRequiredService<IHashStampService>();
            try
            {
                await hashStampService.ProtectLegacyKeysAsync();
            }
            catch (Exception ex)
            {
                // Typically: the root key file is missing while keys depend on it. Nothing below can work.
                _logger.LogCritical(ex, "Server key chain could not be opened. Key rotation is disabled until this is fixed.");
                return;
            }
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                TimeSpan delay;
                using (var scope = _scopeFactory.CreateScope())
                {
                    var hashStampService = scope.ServiceProvider.GetRequiredService<IHashStampService>();
                    var latest = await hashStampService.GetLatestHashAsync();

                    var period = TimeSpan.FromHours(Math.Max(0.01, _securitySettings.CurrentValue.HashStampGenerationPeriod));
                    var due = latest.StampTime + period;

                    if (due <= DateTime.UtcNow)
                    {
                        var stamp = await hashStampService.GenerateAndSaveNewStampAsync();
                        _logger.LogInformation("Server key rotated (stamp {Id}).", stamp.Id);
                        due = stamp.StampTime + period;
                    }

                    delay = due - DateTime.UtcNow;
                }

                if (delay < TimeSpan.FromSeconds(1)) delay = TimeSpan.FromSeconds(1);
                // Re-check at least hourly so a shortened period in the settings takes effect
                if (delay > TimeSpan.FromHours(1)) delay = TimeSpan.FromHours(1);

                await Task.Delay(delay, stoppingToken);
            }
            catch (TaskCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error occurred executing Hash Stamp Generation Job.");
                await Task.Delay(TimeSpan.FromMinutes(30), stoppingToken).ContinueWith(_ => { });
            }
        }

        _logger.LogInformation("Hash Generation Background Job is stopping.");
    }
}
