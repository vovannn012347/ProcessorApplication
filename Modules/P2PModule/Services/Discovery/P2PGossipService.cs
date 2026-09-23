using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using P2PModule.Configuration;
using P2PModule.Infrastructure;
using P2PModule.Interfaces;
using P2PModule.Models;
using P2PModule.Models.Gossips;
using P2PModule.Models.Network;
using P2PModule.Services.Connection;
using P2PModule.Services.Persistence;

namespace P2PModule.Services.Gossip;


/// <summary>
/// Safe JSON deserialization utility class to eliminate try-catch boilerplate out of inbound paths.
/// </summary>
public static class SafeJson
{
    public static T? Deserialize<T>(string? json) where T : class
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<T>(json);
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
/// Manages topic-based gossip message propagation with a single unified, thread-safe priority queue.
/// Leverages the outer P2PPacketEnvelope.Ttl metadata for dynamic hop decay,
/// protecting the signed GossipMessage payload from mutation-induced signature breakage.
/// </summary>
public class P2PGossipService : IHostedService, IP2PGossipService, IDisposable
{
    private readonly ILogger<P2PGossipService> _logger;
    private readonly IOptionsMonitor<GossipSettings> _settings;
    private readonly IP2PRouterService _router;
    private readonly INodeIdentityService _identity;
    private readonly IPeerTracker _peerTracker;

    private readonly ConcurrentDictionary<string, ConcurrentBag<Func<GossipMessage, string, Task>>> _topicSubscriptions = new();
    private readonly ConcurrentDictionary<string, DateTime> _seenGossipIds = new();

    // Single Sorted Priority Queue for background scheduling
    private readonly PriorityQueue<(GossipMessage Message, string SenderId), int> _priorityQueue = new();
    private readonly object _queueLock = new();

    private CancellationTokenSource _schedulerCts = new();
    private Task _queueProcessingTask = Task.CompletedTask;
    private Timer? _cachePruningTimer;

    public P2PGossipService(
        ILogger<P2PGossipService> logger,
        IOptionsMonitor<GossipSettings> settings,
        IP2PRouterService router,
        INodeIdentityService identity,
        IPeerTracker peerTracker)
    {
        _router = router;
        _identity = identity;
        _settings = settings;
        _logger = logger;
        _peerTracker = peerTracker;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _queueProcessingTask = Task.Run(ProcessQueueLoopAsync);
        _cachePruningTimer = new Timer(PruneProcessedCache, null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));

        // Wire incoming WebRTC commands directly into the Ingest path
        _router.RegisterCommandHandler("GossipBroadcastMessage", async (senderId, envelope, cancelToken) =>
        {
            // Exception-free declarative pattern matching
            if (envelope is { SerializedPayload: { Length: > 0 } payloadJson, Ttl: > 0 } &&
                SafeJson.Deserialize<GossipMessage>(payloadJson) is { GossipId: { Length: > 0 } } gossip)
            {
                await IngestGossipAsync(gossip, senderId, envelope.Ttl);
            }

            // returning null tells the P2PRouterService to SKIP sending a default ACK response,
            // preventing expensive, network-wide "echo storm" feedback loops
            return null!;
        });
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _schedulerCts.Cancel();
        _cachePruningTimer?.Dispose();
        _router.UnregisterCommandHandler("GossipBroadcastMessage");
        _topicSubscriptions.Clear();
    }

    public void Subscribe(string topic, Func<GossipMessage, string, Task> handler)
    {
        var bag = _topicSubscriptions.GetOrAdd(topic, _ => new ConcurrentBag<Func<GossipMessage, string, Task>>());
        bag.Add(handler);
    }

    public async Task PublishAsync(string topic, string payload, int priority, string signWithDay)
    {
        var gossipMsg = new GossipMessage
        {
            GossipId = Guid.NewGuid().ToString(),
            Topic = topic,
            SerializedPayload = payload,
            CreatedUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Priority = priority,
            OriginNodeId = _identity.NodeId
        };

        var certBytes = _identity.GetPrivateCertificate(signWithDay)!;
        gossipMsg.Signature = P2PCrypto.SignData(gossipMsg.GetNodeDataHash(), certBytes);

        // Mark as seen locally to block reflecting back to our node
        _seenGossipIds[gossipMsg.GossipId] = DateTime.UtcNow;

        lock (_queueLock)
        {
            _priorityQueue.Enqueue((gossipMsg, _identity.NodeId), priority);
        }

        // Fan out package to neighboring connections with initial envelope TTL = 8
        await FanoutGossipAsync(gossipMsg, initialTtl: 8);
    }

    public async Task PublishAsync(string topic, string payload, int priority)
    {
        var gossipMsg = new GossipMessage
        {
            GossipId = Guid.NewGuid().ToString(),
            Topic = topic,
            SerializedPayload = payload,
            CreatedUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Priority = priority,
            OriginNodeId = _identity.NodeId
        };

        gossipMsg.Signature = P2PCrypto.SignData(gossipMsg.GetNodeDataHash(), _identity.PrivateSignatureKey);

        // Mark as seen locally to block reflecting back to our node
        _seenGossipIds[gossipMsg.GossipId] = DateTime.UtcNow;

        lock (_queueLock)
        {
            _priorityQueue.Enqueue((gossipMsg, _identity.NodeId), priority);
        }

        // Fan out package to neighboring connections with initial envelope TTL = 8
        await FanoutGossipAsync(gossipMsg, initialTtl: 8);
    }

    public async Task IngestGossipAsync(GossipMessage gossip, string senderNodeId, int currentTtl)
    {
        // BROADCAST WALL: If we have already seen or processed this GossipId, block immediately
        if (!_seenGossipIds.TryAdd(gossip.GossipId, DateTime.UtcNow))
        {
            return;
        }

        // ENFORCE STALENESS BOUNDS (Drop expired packets)
        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        long staleLimitMs = _settings.CurrentValue.GossipStaleAgeMs > 0 ? _settings.CurrentValue.GossipStaleAgeMs : 60000;

        if (nowMs - gossip.CreatedUtcMs > staleLimitMs)
        {
            return;
        }

        var node = _peerTracker.GetPeer(gossip.OriginNodeId);
        //if(node == null || !P2PCrypto.TestSignature(gossip.GetNodeDataHash(), node.Identity.PublicVerifyKey, gossip.Signature))
        //{
        //    return;
        //}

        // Queue message for local subscriber processing
        lock (_queueLock)
        {
            _priorityQueue.Enqueue((gossip, senderNodeId), gossip.Priority);
        }

        // 4. Relay only if envelope has remaining hops left
        if (currentTtl > 1)
        {
            await FanoutGossipAsync(gossip, initialTtl: currentTtl - 1);
        }
    }

    private async Task ProcessQueueLoopAsync()
    {
        while (!_schedulerCts.Token.IsCancellationRequested)
        {
            (GossipMessage Message, string SenderId) itemToProcess = default;
            bool hasItem = false;

            lock (_queueLock)
            {
                if (_priorityQueue.TryDequeue(out var queueItem, out _))
                {
                    itemToProcess = queueItem;
                    hasItem = true;
                }
            }

            if (hasItem && itemToProcess.Message != null)
            {
                await ProcessAsync(itemToProcess.Message, itemToProcess.SenderId);
            }
            else
            {
                await Task.Delay(20, _schedulerCts.Token);
            }
        }
    }

    private async Task ProcessAsync(GossipMessage gossip, string senderNodeId)
    {
        if (_topicSubscriptions.TryGetValue(gossip.Topic, out var handlers))
        {
            foreach (var handler in handlers)
            {
                try
                {
                    await handler.Invoke(gossip, senderNodeId);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[Gossip] Subscriber execution failed for topic {Topic}.", gossip.Topic);
                }
            }
        }
    }

    private async Task FanoutGossipAsync(GossipMessage gossip, int initialTtl, string nodeIdExclude = "")
    {
        await _router.FanoutBroadcastMessage("GossipBroadcastMessage", JsonSerializer.Serialize(gossip), new RouteSettings { Ttl = initialTtl });
    }

    private void PruneProcessedCache(object? state)
    {
        var cutoff = DateTime.UtcNow.AddMinutes(
            _settings.CurrentValue.SeenReportCacheTtlMinutes > 0
                ? -_settings.CurrentValue.SeenReportCacheTtlMinutes
                : -30);

        var expiredReportIds = _seenGossipIds
            .Where(kv => kv.Value < cutoff)
            .Select(kv => kv.Key)
            .ToList();

        foreach (var key in expiredReportIds)
        {
            _seenGossipIds.TryRemove(key, out _);
        }
    }

    public void Dispose()
    {
        _schedulerCts.Cancel();
        _cachePruningTimer?.Dispose();
        _router.UnregisterCommandHandler("GossipBroadcastMessage");
        _topicSubscriptions.Clear();
    }
}