using Common.Interfaces.EventBus;
using Common.Models;

using LiteNetLib;

using ProcessorApplication.Sqlite;
using Common.Interfaces;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace ProcessorApplication.Services;

public class MessageHubService : BackgroundService
{
    private class ScheduledAction
    {
        public INodeAction Action { get; set; } = null!;
        public DateTime NextRunAt { get; set; }
        public TimeSpan? Interval { get; set; }
    }

    // External priority list (configure in appsettings.json)
    private static readonly List<string> PriorityOrder = new()
    {
        "Critical", "High", "Normal", "Low", ""
    };

    private readonly IServiceProvider _sp;
    private readonly ILogger<MessageHubService> _log;
    private readonly List<IMessageHandler> _handlers = new();
    private readonly List<ScheduledAction> _actions = new();
    private readonly object _lock = new();

    public MessageHubService(IServiceProvider sp, ILogger<MessageHubService> log)
    {
        _sp = sp;
        _log = log;
    }

    // === MESSAGE ROUTING ===
    public async Task PublishAsync(ActionMessage message, CancellationToken ct = default)
    {
        var handlers = _handlers
            .Where(h => h.GetActions().Contains(message.Action))
            .OrderBy(h => PriorityOrder.IndexOf(GetHandlerPriority(h)))
            .ToList();

        if (!handlers.Any())
            return;

        var tasks = handlers.Select(h => HandleMessageAsync(h, message, ct));
        await Task.WhenAll(tasks);
    }

    private async Task HandleMessageAsync(IMessageHandler handler, ActionMessage message, CancellationToken ct)
    {
        using var scope = _sp.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ContextInstance>();

        try
        {
            _log.LogInformation("Handling message '{Action}' with {Handler}",
                message.Action, handler.GetType().Name);
            await handler.HandleAsync(message, context, ct);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Handler {Handler} failed for action {Action}",
                handler.GetType().Name, message.Action);
        }
    }

    // Called by modules
    public void Schedule(INodeAction action, TimeSpan initialDelay, TimeSpan? interval = null)
    {
        lock (_lock)
        {
            _actions.Add(new ScheduledAction
            {
                Action = action,
                NextRunAt = DateTime.UtcNow.Add(initialDelay),
                Interval = interval
            });
        }
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Load handlers once
        using var scope = _sp.CreateScope();
        _handlers.AddRange(scope.ServiceProvider.GetServices<IMessageHandler>());

        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(5000, ct);

            // Run due actions
            var now = DateTime.UtcNow;
            List<ScheduledAction> due;
            lock (_lock)
            {
                due = _actions.Where(a => a.NextRunAt <= now).ToList();
                foreach (var a in due)
                    a.NextRunAt = a.Interval.HasValue ? now.Add(a.Interval.Value) : DateTime.MaxValue;
            }

            if (due.Any())
            {
                var tasks = due.Select(RunActionAsync);
                await Task.WhenAll(tasks);
            }
        }
    }

    private async Task RunActionAsync(ScheduledAction scheduled)
    {
        using var scope = _sp.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ContextInstance>();

        //this is registerd in P2PNode
        //var manager = scope.ServiceProvider.GetRequiredService<NetManager>();
        try
        {
            _log.LogInformation("Executing action: {Name}", scheduled.Action.Name);
            await scheduled.Action.ProcessAsync(CancellationToken.None, scope, context);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Action failed: {Name}", scheduled.Action.Name);
        }
    }

    private static string GetHandlerPriority(IMessageHandler handler)
    {
        return handler is INodeAction action ? action.GetPriority() : "Normal";
    }
}