using LiteNetLib;

using Microsoft.Extensions.DependencyInjection;
using Common.Models;

using ProcessorApplication.Models;
using Common.Interfaces.EventBus;


namespace ProcessorApplication.Services.p2pNode.p2pActions;

public class CleanupAction : NodeAction
{
    public override async Task Process(
        CancellationToken stoppingToken,
        IServiceScope scope,
        NetManager manager,
        ContextInstance context)
    {
        if (context.TimeStuff.GetLastCleanup() < DateTime.Now)
        {
            PerformCleanup(context, scope, manager, stoppingToken);
            context.TimeStuff.SetLastCleanup( 
                DateTime.Now.AddSeconds(context.Config.Get<double>(P2PSettings.CleanupSeconds_NAME, 600, P2PSettings.SECTION)));
        }

        await Task.CompletedTask;
    }

    private void PerformCleanup(
        ContextInstance context,
        IServiceScope scope,
        NetManager manager, 
        CancellationToken stoppingToken)
    {
        var timespan = context.Config.Get<double>(P2PSettings.CleanupThresholdHours_NAME, 0.2, P2PSettings.SECTION);
        var threshold = DateTime.UtcNow - TimeSpan.FromHours(timespan);

        var trackersDict = context.Trackers;
        var keysToRemove = trackersDict
            .Where(kvp => kvp.Value.LastSeen < threshold)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in keysToRemove)
        {
            trackersDict.Remove(key, out _);
        }

        var peerDict = context.Peers;
        keysToRemove = peerDict
            .Where(kvp => kvp.Value.LastSeen < threshold)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in keysToRemove)
        {
            peerDict.Remove(key, out _);
        }

        //cleanup database? nope, do not think so
        //AppDbContext _context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    }
}