using System.Text;
using System.Text.Json;

using LiteNetLib;

using Microsoft.Extensions.DependencyInjection;
using Common.Models;
using Common.Models.NodeContext;

using ProcessorApplication.Models;
using Common.Interfaces.EventBus;


namespace ProcessorApplication.Services.p2pNode.p2pActions;

public class LocalDIscoveryAction : NodeAction
{
    public override async Task Process(
        CancellationToken stoppingToken,
        IServiceScope scope,
        NetManager manager,
        ContextInstance context)
    {
        if (context.TimeStuff.GetLastAdvertise() < DateTime.Now)
        {
            var messenger = scope.ServiceProvider.GetRequiredService<P2PNodeService>();
            await PerformDiscovery(context, manager, messenger, stoppingToken);
            context.TimeStuff.SetLastAdvertise(
                DateTime.Now.AddSeconds(context.Config.Get<double>(P2PSettings.AdvertiseSeconds_NAME, 600, P2PSettings.SECTION)));
        }
    }

    private async Task PerformDiscovery(
        ContextInstance context,
        NetManager manager,
        P2PNodeService messenger,
        CancellationToken stoppingToken)
    {
        if (stoppingToken.IsCancellationRequested) return;
        //LAN
        await SendPingBroadcast(context, manager, messenger);

        //stun or self-advertisement to closest tracker
        //await TrackerAdvertise(context, manager);
    }

    private async Task SendPingBroadcast(ContextInstance context, NetManager manager, P2PNodeService messenger)
    {
        var broadcastPing = new AdvertisePingMessage
        {
            HashKey = context.Config.Get<string>(LocalDataContext.MyHashKey, ""),
            FriendlyName = context.Config.Get<string>(LocalDataContext.MyFriendlyNameKey, "")
        };
        var json = JsonSerializer.Serialize(broadcastPing);

        messenger.SendBroadcast(json);

        await Task.CompletedTask;
    }
}