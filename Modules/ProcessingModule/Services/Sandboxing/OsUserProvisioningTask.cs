
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.AccessControl;
using System.Security.Principal;

using Common.Interfaces;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Win32.SafeHandles;

using ProcessingModule.Configuration;
using ProcessingModule.Database;
using ProcessingModule.Infrastructure;
using ProcessingModule.Services.Runtime;

namespace ProcessingModule.Services.Sandboxing;

//not usable
public class OsUserProvisioningTask : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<OsUserProvisioningTask> _logger;

    public OsUserProvisioningTask(IServiceProvider serviceProvider, ILogger<OsUserProvisioningTask> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Use a scope to resolve scoped services like IOsUserManagementService
        //using var scope = _serviceProvider.CreateScope();

        //var generalSettings = scope.ServiceProvider.GetRequiredService<IOptions<ProcessorSettings>>().Value;
        //var osSettings = scope.ServiceProvider.GetRequiredService<IOptions<OsSandboxSettings>>().Value;

        //// Condition 1: Is OS Sandboxing the selected mode?
        //if (generalSettings.SandboxingType != SandboxType.OSUser)
        //{
        //    _logger.LogInformation("Active Sandboxing Mode is {Mode}. OS User Provisioning skipped.", generalSettings.SandboxingType);
        //    return;
        //}

        //// Condition 2: Is Auto-Creation enabled?
        //if (!osSettings.AutoCreateProcessingUser)
        //{
        //    _logger.LogInformation("OS User Auto-Creation is disabled. Manual provisioning required.");
        //    return;
        //}

        //var osUserService = scope.ServiceProvider.GetRequiredService<IOsUserManagementService>();

        //_logger.LogInformation("Enacting OS User Provisioning for active sandbox user: {User}", osSettings.UserName);

        //try
        //{
        //    await osUserService.ProvisionUserAsync(msg =>
        //    {
        //        // Relay service logs to the standard .NET logger
        //        _logger.LogInformation("[OS-PROVISION] {Message}", msg);
        //    }, stoppingToken);

        //    _logger.LogInformation("OS User Provisioning completed successfully.");
        //}
        //catch (Exception ex)
        //{
        //    _logger.LogError(ex, "Critical failure during automatic OS user creation.");
        //}
    }
}