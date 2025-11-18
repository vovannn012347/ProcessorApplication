using System.Text.Json;
using System.Text.Json.Nodes;

using ProcessorApplication.Sqlite;
using ProcessorApplication.Sqlite.Models;

using LiteNetLib;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using ProcessorApplication.Models;
using System.Reflection;
using ProcessorApplication.Services;
using Common.Models;
using Common.Interfaces.EventBus;

namespace NodeDiscovery.Services;

public class PingHandler : NodeMessageHandler
{
    public static readonly string[] ACTION_TYPES = new string[] { 
        PingMessage.MESSAGE_TYPE };

    private readonly P2PNodeService _nodeService;
    private readonly ILogger<PingHandler> _logger;

    private readonly ContextInstance _sharedContext;

    public PingHandler(
        P2PNodeService nodeService,
        ILogger<PingHandler> logger)
    {
        //_context = context;
        _nodeService = nodeService;
        _logger = logger;
        _sharedContext = nodeService.SharedContext;
    }

    public override string[] GetActions() => ACTION_TYPES;

    public override async Task HandleAsync(NetPeer sender, string action, string messageStr)
    {
        var ping = JsonSerializer.Deserialize<PingMessage>(messageStr);
        if (ping != null)
        {
            if (!_sharedContext.Peers.TryGetValue(ping.HashKey, out var peerEntry))
            {
                peerEntry = new PeerInstance
                {
                    HashKey = ping.HashKey,
                };

                UpdatePingPeer(sender, ping, peerEntry);

                _sharedContext.Peers.TryAdd(ping.HashKey, peerEntry);// _context.Peers.Add(peerEntry);
            }
            else
            {
                lock (peerEntry)
                {
                    UpdatePingPeer(sender, ping, peerEntry);
                }

                if (peerEntry.Role)
                {
                    if (!_sharedContext.Trackers.TryGetValue(ping.HashKey, out var trackerEntry))
                    {
                        trackerEntry = new TrackerInstance
                        {
                            HashKey = ping.HashKey
                        };
                        UpdateTracker(sender, ping, trackerEntry);

                        _sharedContext.Trackers.TryAdd(ping.HashKey, trackerEntry);
                    }
                    else
                    {
                        lock (trackerEntry)
                        {
                            UpdateTracker(sender, ping, trackerEntry);
                        }
                    }
                }
            }
        }

        _logger.LogInformation("Processed ping from {Sender}: {HashKey}", sender, ping.HashKey);
    }

    private void UpdateTracker(NetPeer sender, PingMessage ping, TrackerInstance trackerEntry)
    {
        trackerEntry.Address = sender.Address.ToString();
        trackerEntry.Port = sender.Port;
        trackerEntry.LastSeen = DateTime.Now;
        trackerEntry.LastPoked = DateTime.Now;
        trackerEntry.Metric = sender.Ping;


        trackerEntry.NodeKeyKnownFrom = ping.HashKey;
    }

    private void UpdatePingPeer(NetPeer sender, PingMessage ping, PeerInstance peerEntry)
    {
        peerEntry.Address = sender.Address.ToString();
        peerEntry.Port = sender.Port;
        peerEntry.LastSeen = DateTime.Now;
        peerEntry.LastPoked = DateTime.Now;
        peerEntry.Metric = sender.Ping;

        peerEntry.NodeKeyKnownFrom = ping.HashKey;
    }
}