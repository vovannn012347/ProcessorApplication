using System.Collections.Generic;

using Common.Interfaces.EventBus;
using Common.Models;

using LiteNetLib;

using Microsoft.Extensions.DependencyInjection;

using ProcessorApplication.Models;
using ProcessorApplication.Models.Nodes;
using ProcessorApplication.Services.p2pNode;


namespace ProcessorApplication.Actions;

public class CleanupAction : NodeAction
{
    public override async Task Process(
        CancellationToken stoppingToken,
        IServiceScope scope,
        NetManager manager,
        ContextInstance context)
    {
        if (context.TimeStuff.GetLastSomething(P2PSettings.CleanupSeconds_NAME) < DateTime.UtcNow)
        {
            PerformCleanup(context, scope, manager, stoppingToken);
            context.TimeStuff.SetLastSomething(
                P2PSettings.CleanupSeconds_NAME,
                DateTime.UtcNow.AddSeconds(context.Config.Get<double>(P2PSettings.CleanupSeconds_NAME, 600, P2PSettings.SECTION)));
        }

        await Task.CompletedTask;
    }

    private void PerformCleanup(
    ContextInstance context,
    IServiceScope scope,
    NetManager manager,
    CancellationToken stoppingToken)
    {
        if (stoppingToken.IsCancellationRequested) return;

        // Initialize parameters
        var threshold = DateTime.UtcNow - TimeSpan.FromHours(
            context.Config.Get<double>(P2PSettings.CleanupThresholdHours_NAME, 0.2, P2PSettings.SECTION));
        var currentRole = context.Config.Get<NodeRole>(P2PSettings.P2PRole_NAME, NodeRole.Peer, P2PSettings.SECTION);
        var kNearest = context.Config.Get<int>(P2PSettings.KNearestNodes_NAME, 3, P2PSettings.SECTION);
        var connectedNodes = context.ConnectedNodes;

        // Separate peers and trackers
        var peerNodes = new List<ConnectionInfo>();
        var trackerNodes = new List<ConnectionInfo>();
        var nodesToCleanup = new List<int>();

        foreach (var nodeRecord in connectedNodes)
        {
            if (!nodeRecord.Value.NetPeerId.HasValue)
            {
                nodesToCleanup.Add(nodeRecord.Key);
                continue;
            }

            switch (nodeRecord.Value.Role)
            {
                case NodeRole.Peer:
                    peerNodes.Add(nodeRecord.Value);
                    break;
                case NodeRole.PeerPromoted:
                case NodeRole.Tracker:
                case NodeRole.ConsensusTracker:
                    trackerNodes.Add(nodeRecord.Value);
                    break;
                default:
                    break;
            }
            //if (nodeRecord.Value.Role == NodeRole.Peer)
            //    peerNodes.Add(nodeRecord.Value);
            //else 
            //if (nodeRecord.Value.Role is NodeRole.PeerPromoted or NodeRole.Tracker or NodeRole.ConsensusTracker)
            //    trackerNodes.Add(nodeRecord.Value);
        }

        // Remove stale nodes without peer IDs
        nodesToCleanup.ForEach(nodeId => connectedNodes.Remove(nodeId, out _));

        // Manage connections for peers and trackers
        int kRequired = currentRole == NodeRole.Peer ? Math.Max(1 - peerNodes.Count(p => p.Connection == ConnectionRole.Forced), 0) : 0;
        ManageConnections(context, manager, peerNodes, kRequired, threshold, nodesToCleanup);

        kRequired = Math.Max(currentRole == NodeRole.Peer ? 1 : kNearest - trackerNodes.Count(p => p.Connection == ConnectionRole.Forced), 0);
        ManageConnections(context, manager, trackerNodes, kRequired, threshold, nodesToCleanup);

        // Disconnect stale or non-essential nodes
        foreach (var nodeId in nodesToCleanup)
        {
            if (connectedNodes.Remove(nodeId, out var node))
                disconnectNode(manager, context, nodeId, node.HashKey);
        }
    }

    private void ManageConnections(
        ContextInstance context,
        NetManager manager,
        List<ConnectionInfo> nodes,
        int kRequired,
        DateTime threshold,
        List<int> nodesToCleanup)
    {
        var managedNodes = new List<NetPeer>();

        // Collect valid peers
        foreach (var node in nodes)
        {
            if (node.NetPeerId.HasValue)
            {
                var peer = manager.GetPeerById(node.NetPeerId.Value);
                if (peer != null)
                    managedNodes.Add(peer);
                else
                    nodesToCleanup.Add(node.NetPeerId.Value);
            }
        }

        // Sort by latency (Ping)
        managedNodes = managedNodes.OrderBy(p => p.Ping).ToList();

        // Manage k-nearest connections
        foreach (var peer in managedNodes)
        {
            if (context.ConnectedNodes.TryGetValue(peer.Id, out var nodeRecord))
            {
                if (kRequired > 0)
                {
                    switch (nodeRecord.Connection)
                    {
                        case ConnectionRole.Inbound:
                            nodeRecord.Connection = ConnectionRole.Mutual;
                            updateNodeContext(context, nodeRecord);
                            kRequired--;
                            break;
                        case ConnectionRole.Mutual:
                        case ConnectionRole.Outbound:
                            kRequired--;
                            break;
                    }
                }
                else
                {
                    switch (nodeRecord.Connection)
                    {
                        case ConnectionRole.Mutual:
                            nodeRecord.Connection = ConnectionRole.Inbound;
                            updateNodeContext(context, nodeRecord);
                            break;
                        case ConnectionRole.Outbound
                            or ConnectionRole.Unknown
                            or ConnectionRole.NotConnected:
                            if (nodeRecord.LastUpdated < threshold)
                                nodesToCleanup.Add(nodeRecord.NetPeerId!.Value);
                            break;
                    }
                }
            }
        }
    }



    private void disconnectNode(NetManager manager, ContextInstance context, int nodeId, string hashKey)
    {
        context.ConnectedNodes.TryRemove(nodeId, out var _);

        if (!string.IsNullOrEmpty(hashKey))
        {
            if (context.Peers.TryGetValue(hashKey, out var node))
            {
                node.NetPeerId = null;
            }

            if (context.Trackers.TryGetValue(hashKey, out var tracker))
            {
                tracker.NetPeerId = null;
            }
        }

        if (manager.TryGetPeerById(nodeId, out var peer))
        {
            peer.Disconnect();
        }
    }

    private void updateNodeContext(ContextInstance context, ConnectionInfo nodeRecord)
    {
        if (!string.IsNullOrEmpty(nodeRecord.HashKey))
        {
            NodeWorking node;
            if (context.Peers.TryGetValue(nodeRecord.HashKey, out node) ||
                context.Trackers.TryGetValue(nodeRecord.HashKey, out node))
            {
                node.NetPeerId = nodeRecord.NetPeerId;
                node.Connection = nodeRecord.Connection;
                node.Role = nodeRecord.Role;
                node.LastSeen = nodeRecord.LastUpdated;
            }
        }
    }

    //private void PerformCleanup(
    //    NodeContextInstance context,
    //    IServiceScope scope,
    //    NetManager manager, 
    //    CancellationToken stoppingToken)
    //{
    //    var timespan = context.Config.Get<double>(P2PSettings.CleanupThresholdHours_NAME, 0.2, P2PSettings.SECTION);
    //    var threshold = DateTime.UtcNow - TimeSpan.FromHours(timespan);

    //    var connectedNodes = context.ConnectedNodes;

    //    List<int> nodesToCleanup = new List<int>();

    //    List<ConnectionInfo> peerNodes = new List<ConnectionInfo>();
    //    List<ConnectionInfo> trackerNodes = new List<ConnectionInfo>();
    //    foreach (var nodeRecord in connectedNodes)
    //    {
    //        if (!nodeRecord.Value.NetPeerId.HasValue)
    //        {
    //            nodesToCleanup.Add(nodeRecord.Key);
    //            continue;
    //        }

    //        switch (nodeRecord.Value.Role)
    //        {
    //            case NodeRole.Peer:
    //                peerNodes.Add(nodeRecord.Value);
    //                break;
    //            case NodeRole.PeerPromoted:
    //            case NodeRole.Tracker:
    //            case NodeRole.ConsensusTracker:
    //                trackerNodes.Add(nodeRecord.Value);
    //                break;
    //            default:
    //                break;
    //        }
    //    }

    //    nodesToCleanup.ForEach(nodeId => connectedNodes.Remove(nodeId, out var _));
    //    nodesToCleanup.Clear();

    //    var currentRole = context.Config.Get<NodeRole>(P2PSettings.P2PRole_NAME, NodeRole.Peer, P2PSettings.SECTION);
    //    var kNearest = context.Config.Get<int>(P2PSettings.KNearestNodes_NAME, 3, P2PSettings.SECTION);

    //    List<NetPeer> peersManaged = new List<NetPeer>();
    //    List<NetPeer> trackersManaged = new List<NetPeer>();

    //    if (currentRole == NodeRole.Peer)
    //    {
    //        //peer
    //        //disconnect from extra peers, account for forced connections
    //        foreach (var nodeRecord in peerNodes)
    //        {
    //            if (nodeRecord.NetPeerId.HasValue)
    //            {
    //                var peer = manager.GetPeerById(nodeRecord.NetPeerId.Value);
    //                if(peer != null)
    //                {
    //                    peersManaged.Add(peer);
    //                }
    //                else
    //                {
    //                    nodesToCleanup.Add(nodeRecord.NetPeerId.Value);
    //                }
    //            }
    //        }

    //        nodesToCleanup.ForEach(nodeId => connectedNodes.Remove(nodeId, out var _));
    //        nodesToCleanup.Clear();

    //        var forcedPeerCount = peerNodes.Where(p => p.Connection == ConnectionRole.Forced).Count();

    //        peersManaged = peersManaged.OrderBy(p => p.Ping).ToList();

    //        int knearestRequired = Math.Max(kNearest - forcedPeerCount, 0);
    //        foreach (var peerManaged in peersManaged)
    //        {
    //            if(knearestRequired > 0)
    //            {
    //                if (connectedNodes.TryGetValue(peerManaged.Id, out var nodeRecord))
    //                {
    //                    switch (nodeRecord.Connection)
    //                    {
    //                        case ConnectionRole.Inbound:
    //                            nodeRecord.Connection = ConnectionRole.Mutual;
    //                            updateNodeContext(context, nodeRecord);
    //                            --knearestRequired;
    //                            break;
    //                        case ConnectionRole.Mutual:
    //                        case ConnectionRole.Outbound:
    //                            --knearestRequired;
    //                            break;
    //                        default:
    //                            break;
    //                    }
    //                }
    //            }
    //            else
    //            {
    //                if (connectedNodes.TryGetValue(peerManaged.Id, out var nodeRecord))
    //                {
    //                    switch (nodeRecord.Connection)
    //                    {
    //                        case ConnectionRole.Mutual:
    //                            //demote connection
    //                            nodeRecord.Connection = ConnectionRole.Inbound;
    //                            updateNodeContext(context, nodeRecord);
    //                            break;
    //                        case ConnectionRole.Outbound:
    //                        case ConnectionRole.Unknown:
    //                        case ConnectionRole.NotConnected:
    //                            if(nodeRecord.LastUpdated < threshold)
    //                                nodesToCleanup.Add(nodeRecord.NetPeerId.Value);
    //                            break;
    //                        case ConnectionRole.Inbound:
    //                        case ConnectionRole.Forced:
    //                        default:
    //                            break;
    //                    }
    //                }
    //            }
    //        }

    //        foreach(var nodeId in nodesToCleanup)
    //        {
    //            if(connectedNodes.Remove(nodeId, out var node))
    //            {
    //                disconnectNode(manager, context, nodeId, node.HashKey);
    //            }
    //        }
    //        nodesToCleanup.Clear();

    //        //===============
    //        //disconnect from extra trackers, account for forced connections
    //        foreach (var nodeRecord in trackerNodes)
    //        {
    //            if (nodeRecord.NetPeerId.HasValue)
    //            {
    //                var peer = manager.GetPeerById(nodeRecord.NetPeerId.Value);
    //                if (peer != null)
    //                {
    //                    trackersManaged.Add(peer);
    //                }
    //                else
    //                {
    //                    nodesToCleanup.Add(nodeRecord.NetPeerId.Value);
    //                }
    //            }
    //        }

    //        //drop stales
    //        nodesToCleanup.ForEach(nodeId => connectedNodes.Remove(nodeId, out var _));
    //        nodesToCleanup.Clear();

    //        //select for disconnect
    //        var forcedTrackerCount = trackerNodes.Where(p => p.Connection == ConnectionRole.Forced).Count();
    //        trackersManaged = trackersManaged.OrderBy(p => p.Ping).ToList();
    //        knearestRequired = Math.Max(1 - forcedTrackerCount, 0);
    //        foreach (var trackerManaged in trackersManaged)
    //        {
    //            if (knearestRequired > 0)
    //            {
    //                if (connectedNodes.TryGetValue(trackerManaged.Id, out var nodeRecord))
    //                {
    //                    switch (nodeRecord.Connection)
    //                    {
    //                        case ConnectionRole.Inbound:
    //                            nodeRecord.Connection = ConnectionRole.Mutual;
    //                            updateNodeContext(context, nodeRecord);
    //                            --knearestRequired;
    //                            break;
    //                        case ConnectionRole.Mutual:
    //                        case ConnectionRole.Outbound:
    //                            --knearestRequired;
    //                            break;
    //                        default:
    //                            break;
    //                    }
    //                }
    //            }
    //            else
    //            {
    //                if (connectedNodes.TryGetValue(trackerManaged.Id, out var nodeRecord))
    //                {
    //                    switch (nodeRecord.Connection)
    //                    {
    //                        case ConnectionRole.Mutual:
    //                            //demote connection
    //                            nodeRecord.Connection = ConnectionRole.Inbound;
    //                            updateNodeContext(context, nodeRecord);
    //                            break;
    //                        case ConnectionRole.Outbound:
    //                        case ConnectionRole.Unknown:
    //                        case ConnectionRole.NotConnected:
    //                            if (nodeRecord.LastUpdated < threshold)
    //                                nodesToCleanup.Add(nodeRecord.NetPeerId.Value);
    //                            break;
    //                        case ConnectionRole.Inbound:
    //                        case ConnectionRole.Forced:
    //                        default:
    //                            break;
    //                    }
    //                }
    //            }
    //        }

    //        //disconnect from stales
    //        foreach (var nodeId in nodesToCleanup)
    //        {
    //            if (connectedNodes.Remove(nodeId, out var node))
    //            {
    //                disconnectNode(manager, context, nodeId, node.HashKey);
    //            }
    //        }
    //        nodesToCleanup.Clear();
    //    }
    //    else
    //    {
    //        //tracker
    //        //disconnect from extra peers, account for forced connections
    //        foreach (var nodeRecord in peerNodes)
    //        {
    //            if (nodeRecord.NetPeerId.HasValue)
    //            {
    //                var peer = manager.GetPeerById(nodeRecord.NetPeerId.Value);
    //                if (peer != null)
    //                {
    //                    peersManaged.Add(peer);
    //                }
    //                else
    //                {
    //                    nodesToCleanup.Add(nodeRecord.NetPeerId.Value);
    //                }
    //            }
    //        }

    //        //drop stales
    //        nodesToCleanup.ForEach(nodeId => connectedNodes.Remove(nodeId, out var _));
    //        nodesToCleanup.Clear();

    //        //select for disconnect
    //        int knearestRequired = 0;
    //        foreach (var peerManaged in peersManaged)
    //        {
    //            if (knearestRequired > 0)
    //            {
    //                if (connectedNodes.TryGetValue(peerManaged.Id, out var nodeRecord))
    //                {
    //                    switch (nodeRecord.Connection)
    //                    {
    //                        case ConnectionRole.Inbound:
    //                            nodeRecord.Connection = ConnectionRole.Mutual;
    //                            updateNodeContext(context, nodeRecord);
    //                            --knearestRequired;
    //                            break;
    //                        case ConnectionRole.Mutual:
    //                        case ConnectionRole.Outbound:
    //                            --knearestRequired;
    //                            break;
    //                        default:
    //                            break;
    //                    }
    //                }
    //            }
    //            else
    //            {
    //                if (connectedNodes.TryGetValue(peerManaged.Id, out var nodeRecord))
    //                {
    //                    switch (nodeRecord.Connection)
    //                    {
    //                        case ConnectionRole.Mutual:
    //                            //demote connection
    //                            nodeRecord.Connection = ConnectionRole.Inbound;
    //                            updateNodeContext(context, nodeRecord);
    //                            break;
    //                        case ConnectionRole.Outbound:
    //                        case ConnectionRole.Unknown:
    //                        case ConnectionRole.NotConnected:
    //                            if (nodeRecord.LastUpdated < threshold)
    //                                nodesToCleanup.Add(nodeRecord.NetPeerId.Value);
    //                            break;
    //                        case ConnectionRole.Inbound:
    //                        case ConnectionRole.Forced:
    //                        default:
    //                            break;
    //                    }
    //                }
    //            }
    //        }

    //        //disconnect from stales
    //        foreach (var nodeId in nodesToCleanup)
    //        {
    //            if (connectedNodes.Remove(nodeId, out var node))
    //            {
    //                disconnectNode(manager, context, nodeId, node.HashKey);
    //            }
    //        }
    //        nodesToCleanup.Clear();

    //        //===============
    //        //disconnect from extra trackers, account for forced connections
    //        foreach (var nodeRecord in trackerNodes)
    //        {
    //            if (nodeRecord.NetPeerId.HasValue)
    //            {
    //                var peer = manager.GetPeerById(nodeRecord.NetPeerId.Value);
    //                if (peer != null)
    //                {
    //                    trackersManaged.Add(peer);
    //                }
    //                else
    //                {
    //                    nodesToCleanup.Add(nodeRecord.NetPeerId.Value);
    //                }
    //            }
    //        }

    //        nodesToCleanup.ForEach(nodeId => connectedNodes.Remove(nodeId, out var _));
    //        nodesToCleanup.Clear();

    //        var forcedTrackerCount = trackerNodes.Where(p => p.Connection == ConnectionRole.Forced).Count();

    //        trackersManaged = trackersManaged.OrderBy(p => p.Ping).ToList();

    //        knearestRequired = Math.Max(kNearest - forcedTrackerCount, 0);
    //        foreach (var trackerManaged in trackersManaged)
    //        {
    //            if (knearestRequired > 0)
    //            {
    //                if (connectedNodes.TryGetValue(trackerManaged.Id, out var nodeRecord))
    //                {
    //                    switch (nodeRecord.Connection)
    //                    {
    //                        case ConnectionRole.Inbound:
    //                            nodeRecord.Connection = ConnectionRole.Mutual;
    //                            updateNodeContext(context, nodeRecord);
    //                            --knearestRequired;
    //                            break;
    //                        case ConnectionRole.Mutual:
    //                        case ConnectionRole.Outbound:
    //                            --knearestRequired;
    //                            break;
    //                        default:
    //                            break;
    //                    }
    //                }
    //            }
    //            else
    //            {
    //                if (connectedNodes.TryGetValue(trackerManaged.Id, out var nodeRecord))
    //                {
    //                    switch (nodeRecord.Connection)
    //                    {
    //                        case ConnectionRole.Mutual:
    //                            //demote connection
    //                            nodeRecord.Connection = ConnectionRole.Inbound;
    //                            updateNodeContext(context, nodeRecord);
    //                            break;
    //                        case ConnectionRole.Outbound:
    //                        case ConnectionRole.Unknown:
    //                        case ConnectionRole.NotConnected:
    //                            if (nodeRecord.LastUpdated < threshold)
    //                                nodesToCleanup.Add(nodeRecord.NetPeerId.Value);
    //                            break;
    //                        case ConnectionRole.Inbound:
    //                        case ConnectionRole.Forced:
    //                        default:
    //                            break;
    //                    }
    //                }
    //            }
    //        }

    //        foreach (var nodeId in nodesToCleanup)
    //        {
    //            if (connectedNodes.Remove(nodeId, out var node))
    //            {
    //                disconnectNode(manager, context, nodeId, node.HashKey);
    //            }
    //        }
    //        nodesToCleanup.Clear();
    //    }
    //}
}