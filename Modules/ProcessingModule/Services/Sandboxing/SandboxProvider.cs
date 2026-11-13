
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using ProcessorApplication.Infrastructure;

using ProcessingModule.Configuration;
using ProcessingModule.Database;
using ProcessingModule.Infrastructure;
using ProcessingModule.Services.Runtime;
namespace ProcessingModule.Services.Sandboxing;

public class SandboxProvider : ISandboxProvider
{
    private readonly IOptionsMonitor<ProcessorSettings> _settings;
    private readonly IEnumerable<ISandboxProcessing> modules;
    private readonly SandboxReadiness _readiness;
    
    public SandboxProvider(
        IOptionsMonitor<ProcessorSettings> settings, 
        IEnumerable<ISandboxProcessing> modules,
        SandboxReadiness readiness)
    {
        this._settings = settings;
        this.modules = modules;
        this._readiness = readiness;
    }

    public async Task<int> GetActiveJobs()
    {
        var counts = await Task.WhenAll(
            modules.Select(m => m.GetActiveJobs())
        );

        return counts.Sum();
    }

    public ISandboxProcessing GetActiveProcessor()
    {
        var settings = _settings.CurrentValue;
        var currentType = settings.SandboxingType;

        // Require Sandboxing: while the last OS-user check (startup or Check Status) failed, don't start steps in it.
        // Never-checked doesn't block; the runner's own logon/permission checks still apply then.
        if (settings.RequireSandboxing && currentType == SandboxType.OSUser && _readiness.Ok == false)
        {
            return new UnavailableSandbox(currentType,
                $"The OS-user sandbox failed its last check ({_readiness.Source}, {_readiness.CheckedUtc:yyyy-MM-dd HH:mm} UTC): " +
                string.Join(" ", _readiness.Failures.Take(3)) +
                " Fix it (Setup User, then Check Status in Processing settings) or turn off Require Sandboxing.");
        }

        return modules.FirstOrDefault(m => m.GetSandboxType() == currentType)
            ?? new UnavailableSandbox(currentType,
                $"Sandbox type '{currentType}' is not available on this operating system " +
                $"({System.Runtime.InteropServices.RuntimeInformation.OSDescription}). Choose another one in Processing settings.");
    }
}