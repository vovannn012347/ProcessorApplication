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

public class GossipNewTrackerAction : INodeAction  //INode = temporary off
{
    private readonly Random _random = new Random();

    public override async Task Process(
        CancellationToken stoppingToken,
        IServiceScope scope,
        NetManager manager,
        ContextInstance context)
    {
        //immediately gossip new stuff
        if (context.Config.Get<bool>(P2PSettings.GossipNewNodes_NAME, true, P2PSettings.SECTION))
        {
            await GossipToNewTrackers(context, manager, stoppingToken);
        }
    }

    private async Task GossipToNewTrackers(ContextInstance context, NetManager manager, CancellationToken stoppingToken)
    {
        var newTrackers = context.NewTrackers;
        var updatedTrackers = context.NewTrackers.Where(p => !string.IsNullOrEmpty(p.Value.HashKey));
        if (updatedTrackers.Count() == 0) return;

        var gossipPeersCount = context.Config.Get<int>(P2PSettings.GossipCount_NAME, 3, P2PSettings.SECTION);

        var selectedNodes = updatedTrackers.Take(gossipPeersCount);

        await Gossip(selectedNodes.Select(n => n.Value).ToArray(), context, manager, stoppingToken);

        foreach (var peer in selectedNodes)
        {
            newTrackers.Remove(peer.Key, out _);
        }
    }
    private async Task Gossip(NodeWorking[] selectedPeers, ContextInstance context, NetManager manager, CancellationToken stoppingToken)
    {
        var knownPeers = context.Trackers.Values;
        if (knownPeers.Count == 0) return;

        var peerList = knownPeers.Select(p => NodeUpdate.FromNode(p)).ToList();

        var gossipMessage = new GossipMessage
        {
            HashKey = context.Config.Get<string>(LocalDataContext.MyHashKey, ""),
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
}