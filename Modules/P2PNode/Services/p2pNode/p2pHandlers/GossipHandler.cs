using System.Text.Json;
using System.Text.Json.Nodes;

using ProcessorApplication.Sqlite;
using ProcessorApplication.Sqlite.Models;

using LiteNetLib;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using ProcessorApplication.Models;
using ProcessorApplication.Services;
using System.Reflection;
using System.Collections.Generic;
using System.Net;
using Common.Models;
using Common.Interfaces.EventBus;

namespace NodeDiscovery.Services;

public class GossipHandler : NodeMessageHandler
{
    public static readonly string[] ACTION_TYPES = new string[] { GossipMessage.MESSAGE_TYPE };

    //private readonly AppDbContext _context;
    private readonly P2PNodeService _nodeService;
    private readonly ILogger<GossipHandler> _logger;

    private readonly ContextInstance _sharedContext;

    public GossipHandler(
        //AppDbContext context,
        P2PNodeService nodeService,
        ILogger<GossipHandler> logger)
    {
        //_context = context;
        //_nodeService = nodeService;
        //_logger = logger;

        _nodeService = nodeService;
        _logger = logger;
        _sharedContext = nodeService.SharedContext;
    }
    public override string[] GetActions() => ACTION_TYPES;

    public override async Task HandleAsync(NetPeer sender, string action, string messageStr)
    {
        var gossip = JsonSerializer.Deserialize<GossipMessage>(messageStr);
        if (gossip != null)
        {
            UpdateDiscovery(sender, gossip);

            foreach(var peerStuff in gossip.Nodes)
            {
                HandleGossipedNode(gossip, peerStuff);
            }
        }

        await Task.CompletedTask;
    }

    private void UpdateDiscovery(NetPeer sender, GossipMessage gossip)
    {
        if (!_sharedContext.Peers.TryGetValue(gossip.HashKey, out var peerEntry))
        {
            peerEntry = new PeerInstance
            {
                HashKey = gossip.HashKey,
            };

            UpdateDiscoveryPeer(sender, gossip, peerEntry);

            _sharedContext.Peers.TryAdd(gossip.HashKey, peerEntry);// _context.Peers.Add(peerEntry);
            _sharedContext.NewPeers.TryAdd(gossip.HashKey, peerEntry);
        }
        else
        {
            lock (peerEntry)
            {
                UpdateDiscoveryPeer(sender, gossip, peerEntry);
            }
        }

        if (gossip.IsTracker)
        {
            if (!_sharedContext.Trackers.TryGetValue(gossip.HashKey, out var trackerEntry))
            {
                trackerEntry = new TrackerInstance
                {
                    HashKey = gossip.HashKey
                };
                UpdateDiscoveryTracker(sender, gossip, trackerEntry);

                _sharedContext.Trackers.TryAdd(gossip.HashKey, trackerEntry);
                _sharedContext.NewTrackers.TryAdd(gossip.HashKey, trackerEntry);
            }
            else
            {
                lock (trackerEntry)
                {
                    UpdateDiscoveryTracker(sender, gossip, trackerEntry);
                }
            }
        }

        _logger.LogInformation("Processed gossip from {Sender}: {HashKey}", sender, gossip.HashKey);
    }

    private void HandleGossipedNode(GossipMessage gossip, NodeUpdate update)
    {
        if(update.IsTracker)
        {
            if (!_sharedContext.Trackers.TryGetValue(update.HashKey, out var trackerEntry))
            {
                trackerEntry = new TrackerInstance
                {
                    HashKey = update.HashKey
                };
                UpdateTracker(gossip, trackerEntry, update);

                _sharedContext.Trackers.TryAdd(update.HashKey, trackerEntry);
                _sharedContext.NewTrackers.TryAdd(update.HashKey, trackerEntry);
            }
            else
            {
                lock (trackerEntry)
                {
                    UpdateTracker(gossip, trackerEntry, update);
                }
            }
        }
        else
        {
            if (!_sharedContext.Peers.TryGetValue(update.HashKey, out var peerEntry))
            {
                peerEntry = new PeerInstance
                {
                    HashKey = update.HashKey,
                };

                UpdatePeer(gossip, peerEntry, update);

                _sharedContext.Peers.TryAdd(update.HashKey, peerEntry);// _context.Peers.Add(peerEntry);
                _sharedContext.NewPeers.TryAdd(update.HashKey, peerEntry);
            }
            else
            {
                lock (peerEntry)
                {
                    UpdatePeer(gossip, peerEntry, update);
                }
            }
        }
    }



    private void UpdateDiscoveryTracker(NetPeer sender, GossipMessage discovery, TrackerInstance trackerEntry)
    {
        trackerEntry.Address = sender.Address.ToString();
        trackerEntry.Port = sender.Port;
        trackerEntry.FriendlyName = discovery.FriendlyName;
        trackerEntry.LastSeen = DateTime.UtcNow;
        trackerEntry.Metric = sender.Ping;
        trackerEntry.IsTracker = discovery.IsTracker;

        //todo: add better logic
        trackerEntry.NodeKeyKnownFrom = discovery.HashKey;
    }

    private void UpdateDiscoveryPeer(NetPeer sender, GossipMessage discovery, PeerInstance peerEntry)
    {
        peerEntry.Address = sender.Address.ToString();
        peerEntry.Port = sender.Port;
        peerEntry.FriendlyName = discovery.FriendlyName;
        peerEntry.LastSeen = DateTime.UtcNow;
        peerEntry.Metric = sender.Ping;
        //peerEntry.Load = discovery.Load;
        peerEntry.Role = discovery.IsTracker;

        //todo: add better logic
        peerEntry.NodeKeyKnownFrom = discovery.HashKey;
    }

    private void UpdatePeer(GossipMessage sender, PeerInstance peerEntry, NodeUpdate update)
    {
        peerEntry.Address = update.Address.ToString();
        peerEntry.Port = update.Port;
        peerEntry.FriendlyName = update.FriendlyName;
        peerEntry.LastSeen = update.DataUpdated;
        peerEntry.LastPoked = DateTime.MinValue;
        peerEntry.Metric = update.Metric;
        peerEntry.Role = update.IsTracker;

        //todo: add better logic
        peerEntry.NodeKeyKnownFrom = sender.HashKey;
    }

    private void UpdateTracker(GossipMessage sender, TrackerInstance trackerEntry, NodeUpdate update)
    {

        trackerEntry.Address = update.Address.ToString();
        trackerEntry.Port = update.Port;
        trackerEntry.FriendlyName = update.FriendlyName;
        trackerEntry.LastSeen = DateTime.UtcNow;
        trackerEntry.Metric = update.Metric;
        //trackerEntry.Load = update.Load;
        trackerEntry.IsTracker = update.IsTracker;

        //todo: add better logic
        trackerEntry.NodeKeyKnownFrom = sender.HashKey;
    }
}