using System.Net;
using System.Text.Json;
using Common.Models;

using ProcessorApplication.Sqlite;
using ProcessorApplication.Sqlite.Models;

using LiteNetLib;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using ProcessorApplication.Models;
using ProcessorApplication.Services;
using Common.Interfaces.EventBus;

namespace NodeDiscovery.Services;

public class DiscoveryHandler : NodeMessageHandler
{
    public static readonly string[] ACTION_TYPES = new string[] { 
        AdvertisePingMessage.MESSAGE_TYPE
    };

   // private readonly AppDbContext _context;
    private readonly P2PNodeService _nodeService;
    private readonly ILogger<DiscoveryHandler> _logger;
    private readonly ContextInstance _sharedContext;
    

    public DiscoveryHandler(
        P2PNodeService nodeService, 
        ILogger<DiscoveryHandler> logger)
    {
        _nodeService = nodeService;
        _logger = logger;
        _sharedContext = nodeService.SharedContext;
    }

    public override string[] GetActions() => ACTION_TYPES;

    public override async Task HandleAsync(IPEndPoint sender, string action, string messageStr)
    {
        var discovery = JsonSerializer.Deserialize<AdvertisePingMessage>(messageStr);
        if (discovery != null)
        {
            UpdateDiscovery(sender, discovery);
        }

        await Task.CompletedTask;
    }

    private void UpdateDiscovery(IPEndPoint sender, AdvertisePingMessage discovery)
    {
        if (!_sharedContext.Peers.TryGetValue(discovery.HashKey, out var peerEntry) ||
            !_sharedContext.NewPeers.ContainsKey(discovery.HashKey))
        {
            peerEntry = new PeerInstance
            {
                HashKey = discovery.HashKey,
            };

            UpdateDiscoveryPeer(sender, discovery, peerEntry);

            //_sharedContext.KnownPeers.TryAdd(discovery.HashKey, peerEntry);// _context.Peers.Add(peerEntry);
            _sharedContext.NewPeers.TryAdd(discovery.HashKey, peerEntry);
        }
        else
        {
            lock (peerEntry)
            {
                UpdateDiscoveryPeer(sender, discovery, peerEntry);
            }
        }

        if (discovery.IsTracker)
        {
            if (!_sharedContext.Trackers.TryGetValue(discovery.HashKey, out var trackerEntry) ||
                !_sharedContext.NewTrackers.ContainsKey(discovery.HashKey))
            {
                trackerEntry = new TrackerInstance
                {
                    HashKey = discovery.HashKey
                };
                UpdateDiscoveryTracker(sender, discovery, trackerEntry);

                //_sharedContext.KnownTrackers.TryAdd(discovery.HashKey, trackerEntry);
                _sharedContext.NewTrackers.TryAdd(discovery.HashKey, trackerEntry);
            }
            else
            {
                lock (trackerEntry)
                {
                    UpdateDiscoveryTracker(sender, discovery, trackerEntry);
                }
            }
        }

        _logger.LogInformation("Processed discovery from {Sender}: {HashKey}", sender, discovery.HashKey);
    }

    private void UpdateDiscoveryTracker(IPEndPoint sender, AdvertisePingMessage discovery, TrackerInstance trackerEntry)
    {
        trackerEntry.Address = discovery.Address.ToString();
        trackerEntry.Port = discovery.Port;
        trackerEntry.FriendlyName = discovery.FriendlyName;
        trackerEntry.LastSeen = DateTime.UtcNow;

        //direct hi
        if(sender is NetPeer peer)
            trackerEntry.Metric = peer.Ping;

        trackerEntry.IsTracker = discovery.IsTracker;

        trackerEntry.NodeKeyKnownFrom = discovery.HashKey;
    }

    private void UpdateDiscoveryPeer(IPEndPoint sender, AdvertisePingMessage discovery, PeerInstance peerEntry)
    {
        peerEntry.Address = sender.Address.ToString();
        peerEntry.Port = sender.Port;
        peerEntry.FriendlyName = discovery.FriendlyName;
        peerEntry.LastSeen = DateTime.UtcNow;

        if (sender is NetPeer peer)
        {
            peerEntry.Metric = peer.Ping;

        }
        else
        {
            peerEntry.NodeKeyKnownFrom = discovery.HashKey;
        }
        peerEntry.Role = discovery.IsTracker;
    }
}