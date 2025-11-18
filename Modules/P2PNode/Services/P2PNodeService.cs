using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

using Common.Code;
using Common.Interfaces;
using Common.Models;
using Common.Models.NodeContext;

using ProcessorApplication.Sqlite;
using ProcessorApplication.Sqlite.Models;

using LiteNetLib;
using LiteNetLib.Utils;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using ProcessorApplication.Models;

using static LiteNetLib.EventBasedNatPunchListener;
using Common.Interfaces.EventBus;
using ProcessorApplication.Models.Nodes;
using ProcessorApplication.Models.Messages;

namespace ProcessorApplication.Services;

/*
 * this is standart node loop, discovers nodes, discovers trackers  
**/
public class P2PNodeService : BackgroundService, 
    INetEventListener, 
    INatPunchListener,
    IPeerAddressChangedListener, 
    IDeliveryEventListener
{
    public const byte MESSAGE_VERSION = 1;

    private string HashKey = String.Empty;
    //net core stuff
    private readonly ILogger<P2PNodeService> _logger;
    private readonly IServiceScopeFactory _scopeFactory;

    private readonly MessageHandlerService _messageService;
    private readonly NetManager _netManager;
    private readonly NatPunchModule _natPunch;

    //general settings
    private readonly ISettingService _settingsService;
    //common p2p settings
    private readonly P2PSettingsService _p2pSettingsService;

    public ContextInstance SharedContext => _sharedContext;

    //shared data to actions
    private readonly ContextInstance _sharedContext;
    private readonly List<NodeAction> _actions;

    public P2PSettings Settings => _p2pSettingsService.CurrentSettings;
    public NetManager Net => _netManager;

    private (string IP, int Port)? _cachedPublicAddress;
    private DateTime _cachedPublicTime = DateTime.MinValue;

    //singleton dependencies go in here
    public P2PNodeService(
        ILogger<P2PNodeService> logger,
        ISettingService settingsService,
        P2PSettingsService p2pSettingsService,
        MessageHandlerService messageService,
        IServiceScopeFactory serviceScopeFactory)
    {
        _p2pSettingsService = p2pSettingsService;
        _settingsService = settingsService;
        _messageService = messageService;
        _logger = logger;
        _scopeFactory = serviceScopeFactory;

        _netManager = new NetManager(this) { 
            NatPunchEnabled = true, 
            BroadcastReceiveEnabled = true };
        _natPunch = _netManager.NatPunchModule;


        _sharedContext = new ContextInstance();
        _actions = new List<NodeAction>();
    }


    public async Task<string> GetOrCreateHashKeyAsync()
    {
        var hashKey = await _settingsService.GetSettingAsync("HashKey");
        if (hashKey != null) return hashKey;

        var localData = $"{Environment.MachineName}:{Guid.NewGuid()}"; // Placeholder
        using var sha256 = SHA256.Create();
        var bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(localData));
        hashKey = Convert.ToBase64String(bytes);

        await _settingsService.SetSettingAsync("HashKey", hashKey, true);

        return hashKey;
    }

    public async Task<string> GetOrCreateFriendlyNameAsync()
    {
        var name = await _settingsService.GetSettingAsync("FriendlyName");
        if (name != null) return name;

        name = $"Node_{Environment.MachineName}";
        await _settingsService.SetSettingAsync("FriendlyName", name);

        return name;
    }

    private async Task PerformStartup(IServiceScope scope)
    {
        _sharedContext.Config.Set<string>(LocalDataContext.MyHashKey, await GetOrCreateHashKeyAsync());
        _sharedContext.Config.Set<string>(LocalDataContext.MyFriendlyNameKey, await GetOrCreateFriendlyNameAsync());

        _sharedContext.Config.LoadSettings(Settings, P2PSettings.SECTION);
        _netManager.Start(Settings.Port);
        _natPunch.Init(this); 
        
        ReloadActions(scope);

        //redo into tartup actions
        await LoadFromDatabase(scope);

        //private readonly (string Ip, int Port)[] _defaultTrackers = { /*load from configuration*/ };
        _logger.LogInformation(
            "Node started on port {Port}, HashKey: {HashKey}", Settings.Port, 
                _sharedContext.Config.Get<string>(LocalDataContext.MyHashKey, ""));
    }

    public void OnNatIntroductionRequest(IPEndPoint localEndPoint, IPEndPoint remoteEndPoint, string token)
    {
        //server-side

    }

    public void OnNatIntroductionSuccess(IPEndPoint targetEndPoint, NatAddressType type, string token)
    {
        //client-side

    }

    private void ReloadActions(IServiceScope scope)
    {
        _actions.Clear();
        _actions.AddRange(scope.ServiceProvider.GetAllServices<NodeAction>());
    }
    private async Task LoadFromDatabase(IServiceScope scope)
    {
        await LoadTrackers(scope);
        await LoadPeers(scope);
    }

    private async Task LoadTrackers(IServiceScope scope)
    {
        //load trackers from database
        AppDbContext dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var dbTrackers = await dbContext.Trackers.ToListAsync();
        foreach (var tracker in dbTrackers)
        {
            var instance = new NodeWorking
            {
                Address = tracker.Address,
                Port = tracker.Port,
                HashKey = tracker.HashKey,
                FriendlyName = tracker.FriendlyName,
                LastSeen = tracker.LastSeen,
                LastPoked = DateTime.MinValue,
                FirstSeen = tracker.FirstSeen,
                Metric = tracker.Metric,
                NodeKnownFrom = tracker.NodeKnownFrom,
                Role = NodeRole.Tracker
            };
            _sharedContext.Trackers.TryAdd(tracker.HashKey, instance);
        }

        //load trackers from config stuff
        var defaultTrackers = await _settingsService.GetSettingAsync<string[]>(
            P2PSettings.DefaultTrackers_NAME,
            new string[] { },
            P2PSettings.SECTION,
            (trackersStr) =>
            {
                return JsonSerializer.Deserialize<string[]>(trackersStr);
            });

        foreach (var trackerAddr in defaultTrackers)
        {
            // Split host and port
            var parts = trackerAddr.Split(':');
            int port = _p2pSettingsService.CurrentSettings.Port;
            if (parts.Length >= 2)
            {
                port = int.Parse(parts[1]);
            }
            string host = parts[0];

            IPAddress? ipAddress;
            if (!IPAddress.TryParse(host, out ipAddress))
            {
                // Not a direct IP, try DNS resolve
                var addresses = Dns.GetHostAddresses(host);
                ipAddress = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork || a.AddressFamily == AddressFamily.InterNetworkV6);
            }

            //adress was resolved or parsed
            if (ipAddress != null)
            {
                var addr = ipAddress.ToString();
                var tracker = dbContext.Trackers.FirstOrDefault(t => t.Address == addr && t.Port == port);

                if (tracker == null)
                {
                    NodeWorking trInst = new NodeWorking
                    {
                        Address = ipAddress.ToString(),
                        Port = port,
                        HashKey = string.Empty,
                        FriendlyName = string.Empty,
                        LastPoked = DateTime.MinValue,
                        LastSeen = DateTime.MinValue,
                        FirstSeen = DateTime.MinValue,
                        Role = NodeRole.Tracker,
                        NodeKnownFrom = await GetOrCreateHashKeyAsync()
                    };

                    _sharedContext.Trackers.TryAdd(trackerAddr, trInst);
                }
            }
            else
            {
                _logger.LogWarning(string.Format("No suitable IPv4/ipv6 address found for {0}.", trackerAddr));
            }
        }
    }


    private async Task LoadPeers(IServiceScope scope)
    {
        AppDbContext dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var dbPeers = await dbContext.Peers.ToListAsync();
        foreach (var peer in dbPeers)
        {
            var instance = new NodeWorking
            {
                Address = peer.Address,
                Port = peer.Port,
                HashKey = peer.HashKey,
                FriendlyName = peer.FriendlyName,
                LastSeen = peer.LastSeen,
                LastPoked = DateTime.MinValue,
                FirstSeen = peer.FirstSeen,
                Metric = peer.Metric,
                NodeKnownFrom = peer.NodeKnownFrom,
                Role = NodeRole.Peer
            };

            _sharedContext.Peers.TryAdd(peer.HashKey, instance);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var scope = _scopeFactory.CreateScope();

        await PerformStartup(scope);

        while (!stoppingToken.IsCancellationRequested)
        {
            var settings = _p2pSettingsService.CurrentSettings;//refresh settings
            AppDbContext _context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            _netManager.PollEvents();
            _natPunch.PollEvents();

            foreach(NodeAction action in _actions)
            {
                await action.Process(stoppingToken, scope, _netManager, _sharedContext);
            }

            await _context.SaveChangesAsync();
            await Task.Delay(TimeSpan.FromSeconds(settings.UpdateSeconds), stoppingToken);
        }
    }

    public void SendBroadcast(string json)
    {
        var settings = _p2pSettingsService.CurrentSettings;//refresh settings

        int port = Settings.Port;
        byte[] jsonBytes = Encoding.UTF8.GetBytes(json);
        byte[] compressedBytes = Compression.Compress(jsonBytes);

        NetDataWriter writer = new NetDataWriter();
        writer.Put((byte)MessageType.Json);
        writer.PutBytesWithLength(compressedBytes);

        _netManager.SendBroadcast(compressedBytes, port);
    }

    //direct peer is known
    public async Task SendJsonAsync(NetPeer peer, string message, DeliveryMethod method)
    {
        byte[] jsonBytes = Encoding.UTF8.GetBytes(message);
        byte[] compressedBytes = Compression.Compress(jsonBytes);

        await Task.Run(() =>
        {
            NetDataWriter writer = new NetDataWriter();
            writer.Put((byte)MessageType.Json);
            writer.PutBytesWithLength(compressedBytes);
            peer.Send(writer, method);
        }).ConfigureAwait(false);
    }

    //direct peer is not known
    public async Task SendByRelay(string targetHashKey, byte[] compressedBytes, DeliveryMethod method, int ttl = -1)
    {
        // Use settings for default TTL
        ttl = ttl >= 0 ? ttl : Settings.RelayTTL;

        // TODO make the actual sending chain
        // Find the peer to send the relay message to
        // use consensus data

        NetPeer? peer = null;
        //NetPeer? peer = 

        //if (_sharedContext.Trackers.TryGetValue(targetHashKey, out var targetNode) ||
        //    _sharedContext.Peers.TryGetValue(targetHashKey, out targetNode))
        //{
        //    // Try direct connection if peer is known
        //    if (targetNode.NetPeerId.HasValue)
        //    {
        //        peer = _netManager.GetPeerById(targetNode.NetPeerId.Value);
        //    }

        //    // Try reconnect if not connected
        //    if (peer == null)
        //    {
        //        peer = _netManager.Connect(targetNode.Address, targetNode.Port, targetNode.HashKey);
        //    }
        //}
        //else
        //{
        //    _logger.LogWarning("Relay target {TargetHashKey} not found in known nodes.", targetHashKey);
        //    return;
        //}

        if (peer != null)
        {
            await Task.Run(() =>
            {
                NetDataWriter writer = new NetDataWriter();
                // Use WriteRelay for optimized binary format
                WriteRelay(writer, targetHashKey, (byte)ttl, compressedBytes);
                peer.Send(writer, method);
            }).ConfigureAwait(false);
            _logger.LogInformation("Relay sent to {Address}:{Port} for {TargetHashKey}", peer.Address, peer.Port, targetHashKey);
        }
        else
        {
            _logger.LogWarning("No available peer connection for relay target {TargetHashKey}.", targetHashKey);
        }
    }

    public async Task SendByRelay(string targetHashKey, string json, DeliveryMethod method, int ttl = -1)
    {
        byte[] jsonBytes = Encoding.UTF8.GetBytes(json);
        byte[] compressedBytes = Compression.Compress(jsonBytes);

        await SendByRelay(targetHashKey, compressedBytes, method, ttl);
    }

    public static void WriteRelay(NetDataWriter writer, string targetHash, byte ttl, byte[] payload)
    {
        writer.Put((byte)MessageType.Relay);
        writer.Put(MESSAGE_VERSION);
        writer.Put(ttl);
        writer.Put((ushort)targetHash.Length);
        writer.Put(targetHash);
        writer.Put(payload.Length);
        writer.Put(payload);
    }

    public static bool TryReadRelay(NetPacketReader reader, out string target, out byte ttl, out byte[] payload)
    {
        target = string.Empty;
        ttl = 0;
        payload = Array.Empty<byte>();
        byte version;

        try
        {
            version = reader.GetByte(); //currently only version one
            ttl = reader.GetByte();
            int len = reader.GetUShort();
            target = reader.GetString(len);
            int payloadLen = reader.GetInt();
            reader.GetBytes(payload, payloadLen);
            return true;
        }
        catch
        {
            return false;
        }
    }


    #region INetEventListener
    public void OnPeerConnected(NetPeer peer)
    {
        if (!_sharedContext.ConnectedNodes.ContainsKey(peer.Id))
        {
            //add material for hello action
            _sharedContext.ConnectedNodes.TryAdd(peer.Id,
                new ConnectionInfo
                {
                    Connection = ConnectionRole.Unknown,
                    LastUpdated = DateTime.UtcNow,
                    HashKey = string.Empty,
                    NetPeerId = peer.Id,
                    Role = NodeRole.Unknown
                });
        }

        //do nothing, wait for hello for peer to identify self
        _logger.LogInformation("Peer connected: {Address}:{Port}", peer.Address, peer.Port);
    }

    public void OnPeerDisconnected(NetPeer peer, DisconnectInfo disconnectInfo)
    {
        //remove identifier from known peers
        if (_sharedContext.ConnectedNodes.TryGetValue(peer.Id, out var info)) {
            info.Connection = ConnectionRole.NotConnected;
            if(_sharedContext.Peers.TryGetValue(info.HashKey, out var peerKnown))
            {
                peerKnown.NetPeerId = null;
            }

            if (_sharedContext.Trackers.TryGetValue(info.HashKey, out var trackerKnown))
            {
                trackerKnown.NetPeerId = null;
            }
        }
        _logger.LogInformation("Peer disconnected: {Address}:{Port}", peer.Address, peer.Port);
    }

    public void OnNetworkError(IPEndPoint endPoint, SocketError socketError)
    {
        _logger.LogWarning("Network error: {EndPoint}, {Error}", endPoint, socketError);
    }

    public void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod deliveryMethod)
    {
        MessageType packetType = (MessageType)reader.GetByte();

        switch (packetType)
        {
            case MessageType.Json:
                {
                    byte[] compressedBytes = reader.GetRemainingBytes();
                    byte[] jsonBytes = Compression.Decompress(compressedBytes);
                    string messageStr = Encoding.UTF8.GetString(jsonBytes);

                    var typeInfo = JsonSerializer.Deserialize<ActionMessage>(messageStr);
                    if (typeInfo != null)
                    {
                        _messageService.HandleMessage(peer, typeInfo, messageStr);
                    }
                    else
                    {
                        if(_sharedContext.ConnectedNodes.TryGetValue(peer.Id, out var peerHash)) 
                        {
                            _logger.LogWarning("Could not parse message from {Peer}:{PeerHash}.", peer.Address, peerHash);
                        }
                        else
                        {
                            _logger.LogWarning("Could not parse message from {Peer}.", peer.Address);
                        }
                    }
                    break;
                }

            case MessageType.Relay:
                {
                    // Use TryReadRelay to parse binary relay message
                    if (TryReadRelay(reader, out string target, out byte ttl, out byte[] payload))
                    {
                        string myHashKey = _sharedContext.Config.Get<string>(LocalDataContext.MyHashKey, "");
                        if (target == myHashKey)
                        {
                            // Message is for this node
                            byte[] jsonBytes = Compression.Decompress(payload);
                            string jsonPayload = Encoding.UTF8.GetString(jsonBytes);

                            var innerType = JsonSerializer.Deserialize<PeekActionMessage>(jsonPayload);
                            if (innerType != null)
                            {
                                _messageService.HandleMessage(peer, innerType, jsonPayload);
                            }
                            else
                            {
                                _logger.LogWarning("Could not parse relay payload from {Peer}.", peer.Address);
                            }
                        }
                        else if (ttl > 0)
                        {
                            // Forward the relay message
                            ttl--; // Decrease TTL
                            SendByRelay(target, payload, DeliveryMethod.ReliableOrdered, ttl);
                        }
                        else
                        {
                            _logger.LogWarning("Dropped relay message (TTL expired) from {Peer}.", peer.Address);
                        }
                    }
                    else
                    {
                        _logger.LogWarning("Failed to parse relay message from {Peer}.", peer.Address);
                    }
                    break;
                }

            default:
                _logger.LogWarning("Unknown message type {Type} from {Peer}.", packetType, peer.Address);
                break;
        }

        reader.Recycle();
    }

    public void OnNetworkReceiveUnconnected(
        IPEndPoint remoteEndPoint,
        NetPacketReader reader,
        UnconnectedMessageType messageType)
    {
        try
        {
            MessageType packetType = (MessageType)reader.GetByte();

            switch (packetType)
            {
                case MessageType.Json:
                    {
                        byte[] compressedBytes = reader.GetRemainingBytes();
                        byte[] jsonBytes = Compression.Decompress(compressedBytes);
                        string messageStr = Encoding.UTF8.GetString(jsonBytes);

                        var typeInfo = JsonSerializer.Deserialize<ActionMessage>(messageStr);
                        if (typeInfo != null)
                        {
                            _messageService.HandleMessage(remoteEndPoint, typeInfo, messageStr);
                        }
                        else
                        {
                            _logger.LogWarning("Could not parse message from {Peer}.", remoteEndPoint);
                        }
                        break;
                    }

                case MessageType.Relay:
                    {
                        // Use TryReadRelay to parse binary relay message
                        if (TryReadRelay(reader, out string target, out byte ttl, out byte[] payload))
                        {
                            string myHashKey = _sharedContext.Config.Get<string>(LocalDataContext.MyHashKey, "");
                            if (target == myHashKey)
                            {
                                // Message is for this node
                                byte[] jsonBytes = Compression.Decompress(payload);
                                string jsonPayload = Encoding.UTF8.GetString(jsonBytes);

                                var innerType = JsonSerializer.Deserialize<PeekActionMessage>(jsonPayload);
                                if (innerType != null)
                                {
                                    _messageService.HandleMessage(remoteEndPoint, innerType, jsonPayload);
                                }
                                else
                                {
                                    _logger.LogWarning("Could not parse relay payload from {Peer}.", remoteEndPoint);
                                }
                            }
                            else if (ttl > 0)
                            {
                                // Forward the relay message
                                ttl--; // Decrease TTL
                                SendByRelay(target, payload, DeliveryMethod.ReliableOrdered, ttl);
                            }
                            else
                            {
                                _logger.LogWarning("Dropped relay message (TTL expired) from {Peer}.", remoteEndPoint);
                            }
                        }
                        else
                        {
                            _logger.LogWarning("Failed to parse relay message from {Peer}.", remoteEndPoint);
                        }
                        break;
                    }

                default:
                    _logger.LogWarning("Unknown unconnected message type {Type} from {Remote}.", packetType, remoteEndPoint);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling unconnected message from {Remote}.", remoteEndPoint);
        }
        finally
        {
            reader.Recycle();
        }
    }
    
    public void OnNetworkLatencyUpdate(NetPeer peer, int latency)
    {
        //sure but no, too much stuff happening, will not update latency too much
        //also will not do the bgp stuff, only relay redirection mostly
        //also will add the nat punch of sorts
        //if (_sharedContext.ConnectedNodes.TryGetValue(peer.Id, out var connectedPeer) && !string.IsNullOrEmpty(connectedPeer.HashKey))
        //{
        //    if (connectedPeer.Role is NodeRole.ConsensusTracker or NodeRole.Tracker or NodeRole.PeerPromoted
        //        && _sharedContext.Trackers.TryGetValue(connectedPeer.HashKey, out var nodeTracker))
        //    {
        //        nodeTracker.Metric = latency;
        //    }
        //    else
        //    if (connectedPeer.Role is NodeRole.Peer
        //        && _sharedContext.Peers.TryGetValue(connectedPeer.HashKey, out var nodePeer))
        //    {
        //        nodePeer.Metric = latency;
        //    }
        //}
    }

    public void OnConnectionRequest(ConnectionRequest request)
    {
        var requestKey = request.Data.GetString();
        if (requestKey != null)
            if(requestKey == _sharedContext.Config.Get<string>(LocalDataContext.MyHashKey, string.Empty))
            {
                request.Accept();
                return;
            }
            else if(ConnectSecret.DeserializeSecret(requestKey) == P2PConstants.Hello)
            {
                request.Accept();
                return;
            }

        request.Reject();
        //todo: authorize
    }

    public void OnPeerAddressChanged(NetPeer peer, IPEndPoint previousAddress)
    {
        if (_sharedContext.ConnectedNodes.TryGetValue(peer.Id, out var connectedPeer) && !string.IsNullOrEmpty(connectedPeer.HashKey))
        {
            if(connectedPeer.Role is NodeRole.ConsensusTracker or NodeRole.Tracker or NodeRole.PeerPromoted
                && _sharedContext.Trackers.TryGetValue(connectedPeer.HashKey, out var nodeTracker))
            {
                nodeTracker.Address = peer.Address.ToString();
                nodeTracker.Port = peer.Port;
            }
            else 
            if(connectedPeer.Role is NodeRole.Peer
                && _sharedContext.Peers.TryGetValue(connectedPeer.HashKey, out var nodePeer))
            {
                nodePeer.Address = peer.Address.ToString();
                nodePeer.Port = peer.Port;
            }
        }
    }

    public void OnMessageDelivered(NetPeer peer, object userData)
    {
        //not sure
    }

    #endregion
}