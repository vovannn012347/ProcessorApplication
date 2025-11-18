using System.Text;
using System.Text.Json;

using ProcessorApplication.Sqlite.Models;

using LiteNetLib;

using Microsoft.Extensions.DependencyInjection;
using Common.Models;
using Common.Models.NodeContext;

using ProcessorApplication.Models;
using Common.Interfaces.EventBus;

namespace ProcessorApplication.Services.p2pNode.p2pActions;

public class HelloTrackerAction : NodeAction
{
    public override async Task Process(
        CancellationToken stoppingToken,
        IServiceScope scope,
        NetManager manager,
        ContextInstance context)
    {
        await HelloAsync(context, manager, stoppingToken);
    }

    private async Task HelloAsync(ContextInstance context, NetManager manager, CancellationToken stoppingToken)
    {
        var hello = new HelloMessage
        {
            HashKey = context.Config.Get<string>(LocalDataContext.MyHashKey, ""),
            FriendlyName = context.Config.Get<string>(LocalDataContext.MyFriendlyNameKey, ""),
            IsTracker = context.Config.Get<bool>(P2PSettings.P2PRole_NAME, false, P2PSettings.SECTION),
        };
        var json = JsonSerializer.Serialize(hello);

        var newTrackers = context.NewTrackers.Where(v => string.IsNullOrEmpty(v.Value.HashKey));
        if (newTrackers.Count() == 0) return;

        foreach (var peer in newTrackers.Select(t => t.Value))
        {
            var netPeer = manager.Connect(peer.Address, peer.Port, HelloMessage.MESSAGE_TYPE);
            if (netPeer != null)
            {
                netPeer.Send(Encoding.UTF8.GetBytes(json), DeliveryMethod.ReliableOrdered);
            }
        }
        await Task.CompletedTask;
    }
}