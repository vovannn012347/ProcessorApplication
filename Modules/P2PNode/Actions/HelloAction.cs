using System.Text;
using System.Text.Json;

using Common.Interfaces.EventBus;
using Common.Models;
using Common.Models.NodeContext;

using LiteNetLib;

using Microsoft.Extensions.DependencyInjection;

using ProcessorApplication.Models;
using ProcessorApplication.Models.Nodes;
using ProcessorApplication.Services;

namespace ProcessorApplication.Actions;

public class HelloAction : NodeAction
{
    public override async Task Process(
        CancellationToken stoppingToken,
        IServiceScope scope,
        NetManager manager,
        ContextInstance context)
    {
        //say hello to unknown role netpeer records
        var messenger = scope.ServiceProvider.GetRequiredService<P2PNodeService>();
        await HelloAsync(context, manager, messenger, stoppingToken);
    }

    private async Task HelloAsync(ContextInstance context, NetManager manager, P2PNodeService messenger, CancellationToken stoppingToken)
    {
        var hello = new HelloMessage
        {
            HashKey = context.Config.Get<string>(LocalDataContext.MyHashKey, ""),
            FriendlyName = context.Config.Get<string>(LocalDataContext.MyFriendlyNameKey, ""),
            Role = context.Config.Get<NodeRole>(P2PSettings.P2PRole_NAME, NodeRole.Peer, P2PSettings.SECTION),
        };
        var json = JsonSerializer.Serialize(hello);

        var helloContacts = context.ConnectedNodes
            .Where(node => node.Value.Role == NodeRole.Unknown);

        if (helloContacts.Count() == 0) return;

        foreach (var peer in helloContacts)
        {
            if (manager.TryGetPeerById(peer.Key, out var netPeer))
            {
                await messenger.SendJsonAsync(netPeer, json, DeliveryMethod.ReliableOrdered);
            }
        }
    }
}