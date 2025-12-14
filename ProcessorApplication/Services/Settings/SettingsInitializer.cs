using Common.Interfaces;

namespace ProcessorApplication.Services.Settings;

public class SettingsInitializer : BackgroundService
{
    private readonly IServiceProvider _sp;
    private readonly ILogger<SettingsInitializer> _log;

    public SettingsInitializer(IServiceProvider sp, ILogger<SettingsInitializer> log)
    {
        _sp = sp;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Run seeding after startup tasks are complete
        await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);

        using var scope = _sp.CreateScope();
        var settingService = scope.ServiceProvider.GetRequiredService<ISettingService>();

        await settingService.SeedDefaultsIfEmptyAsync(stoppingToken);
    }
}