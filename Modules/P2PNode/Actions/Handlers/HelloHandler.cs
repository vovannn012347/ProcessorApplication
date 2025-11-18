using System.Collections.Generic;
using System.Data;
using System.Net;
using System.Text;
using System.Text.Json;

using Common.Interfaces.EventBus;
using Common.Models;

using LiteNetLib;

using Microsoft.Extensions.Logging;

using NodeDiscovery.Services;

using ProcessorApplication.Models;
using ProcessorApplication.Models.Nodes;
using ProcessorApplication.Services;

namespace ProcessorApplication.Actions.Handlers;

//handle only direct hello messages
//order is inbound hello -> return outbound hi -> hello sender recieves hi
public class HelloHandler : NodeMessageHandler
{
    public static readonly string[] ACTION_TYPES = new string[] { 
        HelloMessage.MESSAGE_TYPE, 
        HiMessage.MESSAGE_TYPE 
    };

    private readonly P2PNodeService _nodeService;
    private readonly ILogger<PingHandler> _logger;

    private readonly ContextInstance _sharedContext;

    public HelloHandler(
        P2PNodeService nodeService,
        ILogger<PingHandler> logger)
    {
        _nodeService = nodeService;
        _logger = logger;
        _sharedContext = nodeService.SharedContext;
    }

    public override string[] GetActions() => ACTION_TYPES;


    public override async Task HandleUnconnectedAsync(IPEndPoint sender, string action, string message)
    {
        //not yet
        _logger.LogError("Processed unconnected hello from {Sender}", sender.ToString());
        await Task.CompletedTask;
    }

    public override async Task HandlePeerAsync(NetPeer sender, string action, string message)
    {
        var hello = JsonSerializer.Deserialize<HelloMessage>(message);

        if(hello != null)
        {
            if (action == HelloMessage.MESSAGE_TYPE)
            {
                HandleHello(sender, hello);
                await SendHiResponseAsync(sender, hello); // Send "hi" back
            }
            else
            if (action == HiMessage.MESSAGE_TYPE)
            {
                HandleHi(sender, hello);
            }
        }

        _logger.LogInformation("Processed hello from {Sender}", sender.ToString());
    }

    private void HandleHi(NetPeer sender, HelloMessage hello)
    {
        //hello sender recieves hi
        var hash = hello.HashKey;
        var now = DateTime.UtcNow;

        if(_sharedContext.ConnectedNodes.TryGetValue(sender.Id, out var node))
        {
            if (hello.Role is NodeRole.Tracker or NodeRole.ConsensusTracker or NodeRole.PeerPromoted)
            {
                _sharedContext.Trackers.AddOrUpdate(hash,
                    new NodeWorking
                    {
                        HashKey = hello.HashKey,
                        Address = sender.Address.ToString(),
                        Port = sender.Port,
                        Role = hello.Role,
                        LastSeen = now,
                        FirstSeen = now,
                        LastPoked = now,
                        NetPeerId = sender.Id,
                    },
                (key, instance) =>
                {
                    instance.Address = sender.Address.ToString();
                    instance.Port = sender.Port;
                    instance.Role = hello.Role;
                    instance.LastSeen = now;
                    instance.LastPoked = now;
                    instance.NetPeerId = sender.Id;

                    return instance;
                });
            }
            else
            if (hello.Role is NodeRole.Peer)
            {
                _sharedContext.Peers.AddOrUpdate(hash,
                    new NodeWorking
                    {
                        HashKey = hello.HashKey,
                        Address = sender.Address.ToString(),
                        Port = sender.Port,
                        Role = hello.Role,
                        LastSeen = now,
                        FirstSeen = now,
                        LastPoked = now,
                        NetPeerId = sender.Id,
                    },
                (key, instance) =>
                {
                    instance.Address = sender.Address.ToString();
                    instance.Port = sender.Port;
                    instance.Role = hello.Role;
                    instance.LastSeen = now;
                    instance.LastPoked = now;
                    instance.NetPeerId = sender.Id;

                    return instance;
                });
            }
        }
        else
        {
            _logger.LogWarning("Received hi from unknown node: {HashKey}", hash);
        }
    }

    private void HandleHello(NetPeer sender, HelloMessage hello)
    {
        var hash = hello.HashKey;
        var now = DateTime.UtcNow;

        //we should have a peer to apply the information to
        //and it should be added in onconnected event
        if(_sharedContext.ConnectedNodes.TryGetValue(sender.Id, out var nodeCurrent))
        {
            nodeCurrent.HashKey = hello.HashKey;
            nodeCurrent.LastUpdated = now;
            nodeCurrent.Role = hello.Role;
            nodeCurrent.NetPeerId = sender.Id;
            nodeCurrent.Connection = ConnectionRole.Inbound;

            if (hello.Role is NodeRole.Tracker or NodeRole.ConsensusTracker or NodeRole.PeerPromoted)
            {
                _sharedContext.Trackers.AddOrUpdate(hash,
                    new NodeWorking
                    {
                        HashKey = hello.HashKey,
                        Address = sender.Address.ToString(),
                        Port = sender.Port,
                        Role = hello.Role,
                        LastSeen = now,
                        FirstSeen = now,
                        LastPoked = now,
                        NetPeerId = sender.Id,
                    },
                (key, instance) =>
                {
                    instance.Address = sender.Address.ToString();
                    instance.Port = sender.Port;
                    instance.Role = hello.Role;
                    instance.LastSeen = now;
                    instance.LastPoked = now;
                    instance.NetPeerId = sender.Id;

                    return instance;
                });
            }
            else
            if (hello.Role is NodeRole.Peer)
            {
                _sharedContext.Peers.AddOrUpdate(hash,
                    new NodeWorking
                    {
                        HashKey = hello.HashKey,
                        Address = sender.Address.ToString(),
                        Port = sender.Port,
                        Role = hello.Role,
                        LastSeen = now,
                        FirstSeen = now,
                        LastPoked = now,
                        NetPeerId = sender.Id,
                    },
                (key, instance) =>
                {
                    instance.Address = sender.Address.ToString();
                    instance.Port = sender.Port;
                    instance.Role = hello.Role;
                    instance.LastSeen = now;
                    instance.LastPoked = now;
                    instance.NetPeerId = sender.Id;

                    return instance;
                });
            }
        }

        //_sharedContext.ConnectionMap[hash] = sender;
    }

    private async Task SendHiResponseAsync(NetPeer peer, HelloMessage hello)
    {
        var hashKey = await _nodeService.GetOrCreateHashKeyAsync();
        var name = await _nodeService.GetOrCreateFriendlyNameAsync();

        var hi = new HiMessage
        {
            HashKey = hashKey,
            FriendlyName = name,
            Role = _nodeService.Settings.P2PRole
        };

        string jsonString = JsonSerializer.Serialize(hi);

        await _nodeService.SendJsonAsync(peer, jsonString, DeliveryMethod.ReliableOrdered);
    }
}