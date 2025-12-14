using Microsoft.Extensions.Options;

using ProcessorApplication.Infrastructure;
using ProcessorApplication.Models.Settings;

namespace ProcessorApplication.Services;

public class EmailHealthMonitor : BackgroundService
{
    private readonly IEmailService _emailService;
    private readonly IOptionsMonitor<EmailSettings> _optionsMonitor;

    public EmailHealthMonitor(
        IEmailService emailService, 
        IOptionsMonitor<EmailSettings> optionsMonitor)
    {
        _emailService = emailService;
        _optionsMonitor = optionsMonitor;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await _emailService.VerifyConnectionAsync();

        while (!stoppingToken.IsCancellationRequested)
        {
            var minutes = _optionsMonitor.Get(MainModule.MainId).HealthCheckPeriodMinutes;
            if (minutes < 1) minutes = 1;

            await Task.Delay(TimeSpan.FromMinutes(minutes), stoppingToken);

            await _emailService.VerifyConnectionAsync();
        }
    }
}