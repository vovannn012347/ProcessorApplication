using ProcessorApplication.Sqlite;

using LiteNetLib;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Common.Models;

using ProcessorApplication.Models;
using Common.Interfaces.EventBus;


namespace ProcessorApplication.Services.p2pNode.p2pActions;

//persist known stuff to database
public class PersistKnownNodesAction : NodeAction
{
    public override async Task Process(
        CancellationToken stoppingToken,
        IServiceScope scope,
        NetManager manager,
        ContextInstance context)
    {
        if (context.TimeStuff.GetLastPersist() < DateTime.Now)
        {
            await PersistKnownNodes(context, scope, manager, stoppingToken);
            await PersistKnownTrackers(context, scope, manager, stoppingToken);
            context.TimeStuff.SetLastPersist(
                DateTime.Now.AddSeconds(context.Config.Get<double>(P2PSettings.PersistSeconds_NAME, 600, P2PSettings.SECTION)));
        }
    }

    private async Task PersistKnownNodes(
        ContextInstance context,
        IServiceScope scope,
        NetManager manager,
        CancellationToken stoppingToken)
    {
        AppDbContext _context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var dbPeers = await _context.Peers.ToDictionaryAsync(p => p.HashKey, stoppingToken);

        foreach(var kvp in context.Peers)
        {
            var peerId = kvp.Key;
            var peerValue = kvp.Value;

            if (dbPeers.TryGetValue(peerId, out var existing))
            {
                existing.UpdateFrom(peerValue);
                _context.Update(existing);
            }
            else
            {
                _context.Add(peerValue);
            }
        }

        await _context.SaveChangesAsync();
    }


    private async Task PersistKnownTrackers(
        ContextInstance context,
        IServiceScope scope,
        NetManager manager,
        CancellationToken stoppingToken)
    {
        AppDbContext _context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var dbPeers = await _context.Trackers.ToDictionaryAsync(p => p.HashKey, stoppingToken);

        foreach (var kvp in context.Trackers)
        {
            var peerId = kvp.Key;
            var peerValue = kvp.Value;

            if (dbPeers.TryGetValue(peerId, out var existing))
            {
                existing.UpdateFrom(peerValue);
                _context.Update(existing);
            }
            else
            {
                _context.Add(peerValue);
            }
        }

        await _context.SaveChangesAsync();
    }

}