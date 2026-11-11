
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
namespace ProcessingModule.Services.Sandboxing;

public class SandboxProvider : ISandboxProvider
{
    private readonly IOptionsMonitor<ProcessorSettings> _settings;
    private readonly IEnumerable<ISandboxProcessing> modules;
    
    public SandboxProvider(
        IOptionsMonitor<ProcessorSettings> settings, 
        IEnumerable<ISandboxProcessing> modules)
    {
        this._settings = settings;
        this.modules = modules;
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
        var currentType = _settings.CurrentValue.SandboxingType;

        return modules.FirstOrDefault(m => m.GetSandboxType() == currentType)
            ?? new UnavailableSandbox(currentType,
                $"Sandbox type '{currentType}' is not available on this operating system " +
                $"({System.Runtime.InteropServices.RuntimeInformation.OSDescription}). Choose another one in Processing settings.");
    }
}