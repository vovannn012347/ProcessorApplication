
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using ProcessorApplication.Infrastructure;

using ProcessorModule.Configuration;
using ProcessorModule.Database;
using ProcessorModule.Infrastructure;
namespace ProcessorModule.Services.Sandboxing;

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

        return modules.First(m => m.GetSandboxType() == currentType);
    }
}