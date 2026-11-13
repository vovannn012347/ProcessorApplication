using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using ProcessingModule.Configuration;
using ProcessingModule.Infrastructure;
using ProcessingModule.Services.Runtime;

namespace ProcessingModule.Services.Sandboxing;

/// <summary>
/// ValidateSandboxOnStartup: shortly after startup, when the OS-user sandbox is selected, runs the same check as the
/// Check Status button, writes it to the log and records it in <see cref="SandboxReadiness"/> (shown in Processing
/// settings; with RequireSandboxing a failed check stops OS-user steps from starting). Never stops the app itself.
/// </summary>
public sealed class SandboxStartupCheck : BackgroundService
{
    private static readonly TimeSpan StartDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromMinutes(2);

    private readonly IServiceProvider _services;
    private readonly IOptionsMonitor<ProcessorSettings> _settings;
    private readonly SandboxReadiness _readiness;
    private readonly ILogger<SandboxStartupCheck> _logger;

    public SandboxStartupCheck(IServiceProvider services, IOptionsMonitor<ProcessorSettings> settings,
        SandboxReadiness readiness, ILogger<SandboxStartupCheck> logger)
    {
        _services = services;
        _settings = settings;
        _readiness = readiness;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = _settings.CurrentValue;
        if (!settings.ValidateSandboxOnStartup)
        {
            _logger.LogInformation("Sandbox startup check is off (Validate Sandbox On Startup).");
            return;
        }
        if (settings.SandboxingType != SandboxType.OSUser)
        {
            _logger.LogInformation("Sandbox startup check skipped: sandboxing type is {Type}.", settings.SandboxingType);
            return;
        }

        try { await Task.Delay(StartDelay, stoppingToken); }
        catch (OperationCanceledException) { return; }

        var lines = new List<string>();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        cts.CancelAfter(CheckTimeout);
        try
        {
            using var scope = _services.CreateScope();
            var check = scope.ServiceProvider.GetRequiredService<IOsUserManagementService>();
            await check.CheckStatusAsync(line => { lock (lines) lines.Add(line); }, cts.Token);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
        catch (OperationCanceledException)
        {
            lock (lines) lines.Add($"[ERROR] The sandbox check did not finish within {CheckTimeout.TotalMinutes:0} min.");
        }
        catch (Exception ex)
        {
            lock (lines) lines.Add($"[ERROR] The sandbox check failed: {ex.Message}");
        }

        List<string> result;
        lock (lines) result = lines.ToList();
        _readiness.Record(result, "startup");

        foreach (var line in result)
        {
            if (SandboxReadiness.IsFailure(line)) _logger.LogWarning("[sandbox check] {Line}", line);
            else _logger.LogInformation("[sandbox check] {Line}", line);
        }

        if (_readiness.Ok == true)
            _logger.LogInformation("OS-user sandbox startup check passed ({Warnings} warning(s)).", _readiness.WarningCount);
        else if (settings.RequireSandboxing)
            _logger.LogError("OS-user sandbox startup check FAILED; with Require Sandboxing on, script steps will end in Error " +
                             "until Check Status passes in Processing settings. Failures: {Failures}", string.Join(" | ", _readiness.Failures));
        else
            _logger.LogWarning("OS-user sandbox startup check FAILED (Require Sandboxing is off, so steps still start). Failures: {Failures}",
                               string.Join(" | ", _readiness.Failures));
    }
}
