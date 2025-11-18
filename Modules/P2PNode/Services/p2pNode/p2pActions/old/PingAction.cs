using System.Text;
using System.Text.Json;

using ProcessorApplication.Sqlite.Models;

using LiteNetLib;

using Microsoft.Extensions.DependencyInjection;
using Common.Models;
using Common.Models.NodeContext;

using ProcessorApplication.Models;
using Common.Interfaces.EventBus;
using ProcessorApplication.Models.Nodes;

namespace ProcessorApplication.Services.p2pNode.p2pActions;

public class PingAction : NodeAction
{
    
    private readonly Random _random = new Random();

    public override async Task Process(
        CancellationToken stoppingToken,
        IServiceScope scope,
        NetManager manager,
        ContextInstance context)
    {
        if (context.TimeStuff.GetLastPing() < DateTime.Now)
        {
            await PingPeersAsync(context, manager, stoppingToken);
            context.TimeStuff.SetLastPing( 
                DateTime.Now.AddSeconds(context.Config.Get<double>(P2PSettings.PingSeconds_NAME, 10, P2PSettings.SECTION)));
        }
    }

    private async Task PingPeersAsync(ContextInstance context, NetManager manager, CancellationToken stoppingToken)
    {
        var pingMessage = new PingMessage{
            HashKey = 
            context.Config.Get<string>(LocalDataContext.MyHashKey, ""),
        };
        var json = JsonSerializer.Serialize(pingMessage);

        var pingPercent = context.Config.Get<double>(P2PSettings.PingPercent_NAME, 30, P2PSettings.SECTION);

        var knownPeers = context.Peers.Values;
        var pingAmount = Math.Ceiling(knownPeers.Count * (Math.Clamp(pingPercent, 10, 100) / 100));

        knownPeers = SelectTopWeightedPeers(knownPeers.ToArray(), 
            (int)pingAmount, 
            1.0, 
            1.0, 
            context.Config.Get<double>(P2PSettings.PingSeconds_NAME, 10, P2PSettings.SECTION));

        foreach (var peer in knownPeers)
        {
            var netPeer = manager.Connect(peer.Address, peer.Port, PingMessage.MESSAGE_TYPE);
            if (netPeer != null)
            {
                lock (peer)
                {
                    peer.LastSeen = DateTime.Now;
                    peer.Metric = netPeer.Ping;
                }
                if (stoppingToken.IsCancellationRequested) return;
                netPeer.Send(Encoding.UTF8.GetBytes(json), DeliveryMethod.ReliableOrdered);
            }

            lock (peer)
            {
                peer.LastPoked = DateTime.Now;
            }
            if (stoppingToken.IsCancellationRequested) return;
        }
        await Task.CompletedTask;
    }

    public PeerInstance[] SelectTopWeightedPeers(PeerInstance[] peers, 
        int count, 
        double latencyWeight = 1.0, 
        double timeWeight = 1.0, 
        double timeThresholdSeconds = 30.0)
    {
        if (peers == null || peers.Length == 0 || count <= 0)
            return Array.Empty<PeerInstance>();
        count = Math.Min(count, peers.Length);

        var now = DateTime.UtcNow;
        var weightedPeers = peers
            .Select(p => new
            {
                Peer = p,
                Weight = CalculateWeight(p, now, latencyWeight, timeWeight, timeThresholdSeconds)
            })
            .OrderByDescending(p => p.Weight * _random.NextDouble()) // Randomize within weight
            .Take(count)
            .Select(p => p.Peer)
            .ToArray();

        return weightedPeers;
    }

    private double CalculateWeight(NodeWorking peer, DateTime now, 
        double latencyWeight, 
        double timeWeight, 
        double timeThresholdSeconds)
    {
        // Latency weight: Lower latency = higher weight
        double latencyScore = 1.0 / (peer.Metric + 1);
        // Time weight: Increase if not queried recently
        double timeSinceQuerySeconds = (now - peer.LastPoked).TotalSeconds;
        double timeScore = timeSinceQuerySeconds > timeThresholdSeconds
            ? timeSinceQuerySeconds / timeThresholdSeconds
            : 1.0;

        return (latencyWeight * latencyScore) + (timeWeight * timeScore);
    }
}