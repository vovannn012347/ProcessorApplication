using System.Text.Json;

using Common.Interfaces.EventBus;
using Common.Models;
using Common.Models.NodeContext;

using LiteNetLib;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using ProcessorApplication.Models;
using ProcessorApplication.Models.Nodes;


namespace ProcessorApplication.Services.p2pNode.p2pActions;

public class TrackerFindAction : NodeAction
{
    private readonly ILogger<TrackerFindAction> _logger; // Add logger for debugging

    public TrackerFindAction(ILogger<TrackerFindAction> logger)
    {
        _logger = logger;
    }

    public override async Task Process(
        CancellationToken stoppingToken,
        IServiceScope scope,
        NetManager manager,
        ContextInstance context)
    {
        if (context.TimeStuff.GetLastCleanup() < DateTime.Now)
        {
            var messenger = scope.ServiceProvider.GetRequiredService<P2PNodeService>();

            await PerformOrdering(context, manager, messenger, stoppingToken);

            context.TimeStuff.SetLastAdvertise(
                DateTime.Now.AddSeconds(context.Config.Get<double>(
                    P2PSettings.CleanupSeconds_NAME, 600, P2PSettings.SECTION)));
        }
    }

    private void FindATracker(
        ContextInstance context,
        NetManager manager,
        P2PNodeService messenger)
    {

    }

    private async Task PerformOrdering(
        ContextInstance context,
        NetManager manager,
        P2PNodeService messenger,
        CancellationToken stoppingToken)
    {
        //perform tracker selection and general cleanup

        if (stoppingToken.IsCancellationRequested) return;

        var role = context.Config.Get<NodeRole>(P2PSettings.P2PRole_NAME, NodeRole.PeerPromoted, P2PSettings.SECTION);
        switch (role)
        {
            case NodeRole.PeerPromoted:
                //test for a tracker, if found - demote self, inform other peers about tracker
            case NodeRole.Peer:
                //search for a tracker
                //if no tracker - promote self
                string currentTrackerHashKey = context.Config.Get<string>("ClosestTrackerHashKey", "");
                if (!string.IsNullOrEmpty(currentTrackerHashKey) && //we have a tracker
                    context.Trackers.TryGetValue(currentTrackerHashKey, out NodeWorking currentTracker) && // we know about tracker
                    currentTracker.NetPeerId.HasValue && // we know we are connected
                    manager.TryGetPeerById(currentTracker.NetPeerId.Value, out NetPeer trackerPeer)) //and we ARE conencted
                {

                    //nothing, we have a tracker, we do not care
                }
                else
                {
                    bool trackerFound = false;
                    int closestTracker = int.MaxValue;
                    int closestTrackerId = -1;
                    List<int> peersToCleanup = new List<int>();
                    foreach (var tracker in context.Trackers.Concat(context.Trackers))
                    {
                        if (stoppingToken.IsCancellationRequested) break;

                        try
                        {
                            var node = tracker.Value;
                            if (string.IsNullOrEmpty(node.Address) || node.Port == 0) continue;

                            NetPeer? peer = null;
                            if (node.NetPeerId.HasValue)
                            {
                                peer = manager.GetPeerById(node.NetPeerId.Value);
                            }

                            if (peer == null)
                            {
                                if (!string.IsNullOrEmpty(node.HashKey))
                                {
                                    //prepare to say hi and hello later on
                                    peer = manager.Connect(node.Address, node.Port, node.HashKey);
                                }
                                else
                                {
                                    peer = manager.Connect(node.Address, node.Port, node.HashKey);
                                }
                            }

                            //connected?
                            if (peer != null)
                            {
                                if (context.ConnectedNodes.TryGetValue(peer.Id, out var existingTracker))
                                {
                                    if (existingTracker.Connection == ConnectionRole.Forced)
                                    {
                                        trackerFound = true;
                                        closestTracker = peer.Ping;
                                        closestTrackerId = peer.Id;
                                        break;
                                    }
                                }
                                else
                                {
                                    peersToCleanup.Add(peer.Id);
                                }

                                if (peer.Ping < closestTracker)
                                {
                                    trackerFound = true;
                                    closestTracker = peer.Ping;
                                    closestTrackerId = peer.Id;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Error contacting tracker {HashKey}", tracker.Key);
                        }
                    }

                    if (trackerFound)
                    {
                        //get peer info
                        peersToCleanup.Remove(closestTrackerId);

                        foreach (var disconnectPeer in peersToCleanup)
                        {
                            manager.DisconnectPeer(manager.GetPeerById(disconnectPeer));
                        }

                        if (!context.ConnectedNodes.ContainsKey(closestTrackerId))
                        {

                        }
                    }
                }

                    

                break;
                break;
            case NodeRole.Tracker:
            case NodeRole.ConsensusTracker: //consensus promotion happens in separate action
                //select k-nearest trackers

                break;

        }

        //maintain conenction to nearest trackers

        //string myHashKey = context.Config.Get<string>(LocalDataContext.MyHashKey, "");
        //int myPort = context.Config.Get<int>(P2PSettings.Port_NAME, 0, P2PSettings.SECTION);

        //var request = new ClosestTrackerRequest
        //{
        //    SenderHashKey = myHashKey,
        //    //SenderAddress = myAddress, //obtained from request
        //    SenderPort = myPort,
        //    HopCount = 0
        //};

        //var json = JsonSerializer.Serialize(request);
        //bool trackerFound = false;
        //string currentTrackerHashKey = context.Config.Get<string>("ClosestTrackerHashKey", "");
        //string currentTrackerAddress = string.Empty;
        //int currentTrackerPort = 0;

        // Try known trackers
        // If no tracker found, try default trackers from config
        //if (!trackerFound)
        //{
        //    var defaultTrackers = context.Config.Get<string[]>(P2PSettings.DefaultTrackers_NAME, Array.Empty<string>(), P2PSettings.SECTION);
        //    foreach (var trackerAddr in defaultTrackers)
        //    {
        //        if (stoppingToken.IsCancellationRequested) break;

        //        var parts = trackerAddr.Split(':');
        //        string host = parts[0];
        //        int port = parts.Length > 1 ? int.Parse(parts[1]) : context.Config.Get<int>(P2PSettings.Port_NAME, 0, P2PSettings.SECTION);

        //        try
        //        {
        //            var peer = manager.Connect(host, port, myHashKey);
        //            if (peer != null)
        //            {
        //                _logger.LogInformation("Sending ClosestTrackerRequest to default tracker {Address}:{Port}", host, port);
        //                await messenger.SendJsonAsync(peer, json, DeliveryMethod.ReliableOrdered);
        //                await Task.Delay(1000, stoppingToken);

        //                var closestTrackerHashKey = context.Config.Get<string>("ClosestTrackerHashKey", "");
        //                if (!string.IsNullOrEmpty(closestTrackerHashKey))
        //                {
        //                    var closestTracker = context.Trackers.GetValueOrDefault(closestTrackerHashKey) ??
        //                                         context.NewTrackers.GetValueOrDefault(closestTrackerHashKey);
        //                    if (closestTracker != null)
        //                    {
        //                        trackerFound = true;
        //                        break;
        //                    }
        //                }
        //            }
        //        }
        //        catch (Exception ex)
        //        {
        //            _logger.LogWarning(ex, "Error contacting default tracker {Address}:{Port}", host, port);
        //        }
        //    }
        //}

        //if (!trackerFound)
        //{
        //    _logger.LogWarning("No trackers found, considering self-promotion to tracker.");
        //    // Placeholder for self-promotion logic (as in JavaScript Node.discoverClosestTracker)
        //}
        //else
        //{
        //    _logger.LogInformation("Closest tracker set to {HashKey} at {Address}:{Port}",
        //        currentTrackerHashKey, currentTrackerAddress, currentTrackerPort);
        //}

        // Send broadcast ping for LAN discovery (unchanged)
        await SendPingBroadcast(context, manager, messenger);
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