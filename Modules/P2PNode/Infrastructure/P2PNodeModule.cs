using System.Reflection;

using Common.Models;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using ProcessorApplication.Services;

namespace ProcessorApplication.Infrastructure;

public class ModuleActionInitializer : BackgroundService
{
    private readonly IServiceProvider _sp;
    private readonly Assembly _assembly;

    public ModuleActionInitializer(IServiceProvider sp, Assembly assembly)
    {
        _sp = sp;
        _assembly = assembly;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await Task.Delay(1000, ct); // wait for hub

        using var scope = _sp.CreateScope();
        var hub = scope.ServiceProvider.GetRequiredService<MessageHubService>();
        var context = scope.ServiceProvider.GetRequiredService<ContextInstance>();

        // Store hub for actions to find
        AppDomain.CurrentDomain.SetData(nameof(MessageHubService), hub);

        var actions = _assembly.GetTypes()
            .Where(t => typeof(INodeAction).IsAssignableFrom(t) && !t.IsAbstract)
            .Select(t => (INodeAction)scope.ServiceProvider.GetRequiredService(t));

        foreach (var action in actions)
        {
            await action.InitializeAsync(context, ct);
        }
    }
}