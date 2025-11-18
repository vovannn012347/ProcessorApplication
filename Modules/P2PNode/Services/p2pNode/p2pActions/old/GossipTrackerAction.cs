using System;
using System.Text;
using System.Text.Json;

using ProcessorApplication.Sqlite;
using ProcessorApplication.Sqlite.Models;

using LiteNetLib;

using Microsoft.Extensions.DependencyInjection;

using Common.Attributes;
using Common.Models;
using Common.Models.NodeContext;
using ProcessorApplication.Services.p2pNode;

using ProcessorApplication.Models;
using Common.Interfaces.EventBus;
using ProcessorApplication.Models.Nodes;


namespace ProcessorApplication.Services.p2pNode.p2pActions;

public class GossipTrackerAction : NodeAction
{
    private readonly Random _random = new Random();

    public override async Task Process(
        CancellationToken stoppingToken,
        IServiceScope scope,
        NetManager manager,
        ContextInstance context)
    {
        if (context.TimeStuff.GetLastTrackerGossip() < DateTime.Now)
        {
            await PerformStandartTrackerGossipAsync(context, manager, stoppingToken);
            context.TimeStuff.SetLastTrackerGossip(
                DateTime.Now.AddSeconds(context.Config.Get<double>(P2PSettings.GossipIntervalSeconds_NAME, 60, P2PSettings.SECTION)));
        }
    }

    private async Task PerformStandartTrackerGossipAsync(
        ContextInstance context,
        NetManager manager,
        CancellationToken stoppingToken)
    {
        var knownPeers = context.Trackers;
        if (knownPeers.Count == 0) return;

        var gossipPeersCount = context.Config.Get<int>(P2PSettings.GossipCount_NAME, 3, P2PSettings.SECTION);

        var selectedPeers = SelectTopWeightedPeers(
            knownPeers.Values.ToArray(),
            gossipPeersCount,
            context.Config.Get<double>(P2PSettings.GossipIntervalSeconds_NAME, 60, P2PSettings.SECTION));

        await Gossip(selectedPeers, context, manager, stoppingToken);

    }

    private async Task Gossip(NodeWorking[] selectedPeers, ContextInstance context, NetManager manager, CancellationToken stoppingToken)
    {
        var knownPeers = context.Trackers.Values;
        if (knownPeers.Count == 0) return;

        var peerList = knownPeers.Select(p => NodeUpdate.FromNode(p)).ToList();

        var gossipMessage = new GossipMessage
        {
            HashKey = context.Config.Get<string>(LocalDataContext.MyHashKey, ""),
            //Port = context.Config.Get<int>(P2PSettings.Port_NAME, 5000, P2PSettings.SECTION),
            FriendlyName = context.Config.Get<string>(LocalDataContext.MyFriendlyNameKey, ""),
            IsTracker = context.Config.Get<bool>(P2PSettings.P2PRole_NAME, false, P2PSettings.SECTION),
            Nodes = peerList
        };

        var json = JsonSerializer.Serialize(gossipMessage);

        foreach (var peer in selectedPeers)
        {
            var netPeer = manager.Connect(peer.Address, peer.Port, GossipMessage.MESSAGE_TYPE);
            if (netPeer != null)
            {
                netPeer.Send(Encoding.UTF8.GetBytes(json), DeliveryMethod.ReliableOrdered);
            }
        }
        await Task.CompletedTask;
    }

    public NodeWorking[] SelectTopWeightedPeers(NodeWorking[] peers,
        int count,
        double latencyWeight = 1.0,
        double timeWeight = 1.0,
        double timeThresholdSeconds = 30.0)
    {
        if (peers == null || peers.Length == 0 || count <= 0)
            return Array.Empty<Peer>();
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

        double timeScore = timeSinceQuerySeconds / timeThresholdSeconds;

        return (latencyWeight * latencyScore) + (timeWeight * timeScore);
    }
}