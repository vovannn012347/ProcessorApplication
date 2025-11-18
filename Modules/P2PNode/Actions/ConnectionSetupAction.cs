using System.Diagnostics;
using System.Text.Json;
using Common.Models;
using Common.Models.NodeContext;

using ProcessorApplication.Sqlite.Models;

using LiteNetLib;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using ProcessorApplication.Models;
using ProcessorApplication.Services;
using ProcessorApplication.Services.p2pNode;
using Common.Interfaces.EventBus;
using ProcessorApplication.Models.Nodes;

namespace ProcessorApplication.Actions;

public class ConnectionSetupAction : NodeAction
{
    private readonly ILogger<ConnectionSetupAction> _logger; // Add logger for debugging

    public ConnectionSetupAction(ILogger<ConnectionSetupAction> logger)
    {
        _logger = logger;
    }

    public override async Task Process(
        CancellationToken stoppingToken,
        IServiceScope scope,
        NetManager manager,
        ContextInstance context)
    {
        if (context.TimeStuff.GetLastSomething(P2PSettings.TrackerSetupSeconds_NAME) < DateTime.UtcNow)
        {
            var messenger = scope.ServiceProvider.GetRequiredService<P2PNodeService>();

            await PerformConnections(context, manager, messenger, stoppingToken);

            context.TimeStuff.SetLastSomething(
                P2PSettings.TrackerSetupSeconds_NAME,
                DateTime.UtcNow.AddSeconds(context.Config.Get<double>(P2PSettings.CleanupSeconds_NAME, 600, P2PSettings.SECTION)));
        }
    }
    private async Task PerformConnections(
        ContextInstance context,
        NetManager manager,
        P2PNodeService messenger,
        CancellationToken stoppingToken)
    {
        if (stoppingToken.IsCancellationRequested) return;

        var currentRole = context.Config.Get<NodeRole>(P2PSettings.P2PRole_NAME, NodeRole.Peer, P2PSettings.SECTION);
        var kNearest = context.Config.Get<int>(P2PSettings.KNearestNodes_NAME, 3, P2PSettings.SECTION);
        var myHashKey = context.Config.Get<string>(LocalDataContext.MyHashKey, "");

        // Count connected trackers (including forced)
        int connectedTrackerCount = context.ConnectedNodes.Values
            .Count(n => n.Role is NodeRole.Tracker or NodeRole.ConsensusTracker or NodeRole.PeerPromoted &&
                        n.NetPeerId.HasValue &&
                        manager.GetPeerById(n.NetPeerId.Value)?.ConnectionState == ConnectionState.Connected);

        // Combine known and new trackers, excluding self
        var trackers = context.Trackers
            .Where(t => t.Value.HashKey != myHashKey)
            .ToList();

        bool setupCleanup = false;
        //trackers part
        if (currentRole == NodeRole.Peer)
        {
            // For peers: do nothing if any trackers are connected
            if (!(connectedTrackerCount > 0))
            {
                setupCleanup = true;

                // Connect to all trackers if none are connected
                await ConnectToTrackers(context, manager, trackers, stoppingToken);
            }
            else
            {
                _logger.LogDebug("Peer has {Count} connected trackers, skipping tracker selection.", connectedTrackerCount);
            }
        }
        else 
        if (currentRole is NodeRole.Tracker or NodeRole.ConsensusTracker or NodeRole.PeerPromoted)
        {
            // For trackers: connect to more trackers if below k-nearest
            if (connectedTrackerCount < kNearest)
            {
                setupCleanup = true;
                await ConnectToTrackers(context, manager, trackers, stoppingToken);
            }
            else
            {
                _logger.LogDebug("Tracker has {Count} connected trackers, sufficient for k={KNearest}.", connectedTrackerCount, kNearest);
            }
        }

        if (stoppingToken.IsCancellationRequested) return;
        //peer part
        if (currentRole == NodeRole.Peer)
        {
            // Count connected trackers (including forced)
            int connectedPeerCount = context.ConnectedNodes.Values
                .Count(n => n.Role is NodeRole.Peer &&
                        n.NetPeerId.HasValue &&
                        manager.GetPeerById(n.NetPeerId.Value)?.ConnectionState == ConnectionState.Connected);

            var peers = context.Peers.Where(t => t.Value.HashKey != myHashKey).ToList();

            if (connectedPeerCount < kNearest)
            {
                await ConnectToPeers(context, manager, peers, kNearest, stoppingToken);
            }
            else
            {
                _logger.LogDebug("Peer has {Count} connected peers, skipping peer connection.", connectedTrackerCount);
            }
        }


        if (setupCleanup)
        {
            //set up immidiate cleanup to leave only single tracker
            //or k-nearest peers
            //leave two seconds for connection
            context.TimeStuff.SetLastSomething(
                    P2PSettings.CleanupSeconds_NAME,
                    DateTime.UtcNow.AddSeconds( 2 - context.Config.Get<double>(P2PSettings.CleanupSeconds_NAME, 600, P2PSettings.SECTION)));

        }


        // Self-promotion if no trackers are available and none connected
        if (trackers.Count == 0 && connectedTrackerCount == 0)
        {
            _logger.LogInformation("No trackers available and none connected, promoting to PeerPromoted role.");
            context.Config.Set(P2PSettings.P2PRole_NAME, NodeRole.PeerPromoted, P2PSettings.SECTION);
            context.Config.Set(P2PConstants.ClosestTrackerHashKey, myHashKey);
        }
    }

    private async Task ConnectToPeers(ContextInstance context, 
        NetManager manager, 
        List<KeyValuePair<string, NodeWorking>> peers, 
        int amount,
        CancellationToken stoppingToken)
    {
        foreach (var peer in peers)
        {
            if (stoppingToken.IsCancellationRequested) break;

            // Skip if already connected
            if (peer.Value.NetPeerId.HasValue &&
                context.ConnectedNodes.TryGetValue(peer.Value.NetPeerId.Value, out var node) &&
                manager.GetPeerById(peer.Value.NetPeerId.Value)?.ConnectionState == ConnectionState.Connected)
            {
                _logger.LogDebug("Peer {HashKey} already connected, skipping.", peer.Value.HashKey);
                continue;
            }

            try
            {
                if (!string.IsNullOrEmpty(peer.Value.Address))
                {

                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error initiating connection to peer {HashKey}", peer.Value.HashKey);
            }
        }

        await Task.Delay(100, stoppingToken); // Short delay to initiate connections
    }

    private async Task ConnectToTrackers(
        ContextInstance context,
        NetManager manager,
        List<KeyValuePair<string, NodeWorking>> trackers,
        CancellationToken stoppingToken)
    {
        // default trackers are already in
        foreach (var tracker in trackers)
        {
            if (stoppingToken.IsCancellationRequested) break;

            // Skip if already connected
            if (tracker.Value.NetPeerId.HasValue &&
                context.ConnectedNodes.TryGetValue(tracker.Value.NetPeerId.Value, out var node) &&
                manager.GetPeerById(tracker.Value.NetPeerId.Value)?.ConnectionState == ConnectionState.Connected)
            {
                _logger.LogDebug("Tracker {HashKey} already connected, skipping.", tracker.Value.HashKey);
                continue;
            }

            try
            {
                if (!string.IsNullOrEmpty(tracker.Value.Address))
                {
                    var peer = manager.Connect(tracker.Value.Address, tracker.Value.Port, !string.IsNullOrEmpty(tracker.Value.HashKey) ? tracker.Value.HashKey : ConnectSecret.SerializeSecret(P2PConstants.Hello));
                    if (peer != null)
                    {
                        // Add to ConnectedNodes with Outbound role
                        context.ConnectedNodes.AddOrUpdate(
                            peer.Id,
                            new ConnectionInfo
                            {
                                HashKey = tracker.Value.HashKey,
                                Role = tracker.Value.Role,
                                Connection = ConnectionRole.Outbound,
                                NetPeerId = peer.Id,
                                LastUpdated = DateTime.UtcNow
                            },
                            (id, old) => new ConnectionInfo
                            {
                                HashKey = old.HashKey,
                                Role = old.Role,
                                Connection = old.Connection == ConnectionRole.Inbound ? ConnectionRole.Mutual : ConnectionRole.Outbound,
                                NetPeerId = id,
                                LastUpdated = DateTime.UtcNow
                            });
                        _logger.LogInformation("Initiated connection to tracker {HashKey} at {Address}:{Port}",
                            tracker.Value.HashKey, tracker.Value.Address, tracker.Value.Port);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error initiating connection to tracker {HashKey}", tracker.Value.HashKey);
            }
        }

        // Allow time for connections to establish (non-blocking)
        await Task.Delay(100, stoppingToken); // Short delay to initiate connections
    }
}