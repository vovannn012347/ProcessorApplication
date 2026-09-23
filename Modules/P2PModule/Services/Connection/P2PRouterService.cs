using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;

using P2PModule.Interfaces;
using P2PModule.Models;
using P2PModule.Models.Network;
using P2PModule.Services.Discovery;
using P2PModule.Services.Persistence;

namespace P2PModule.Services.Connection;


/// <summary>
/// Encapsulates the execution policy parameters for routed P2P transactions.
/// </summary>
public class RouteSettings
{
    /// <summary>
    /// The maximum duration allowed before a standard request or chunk inactivity timeout fires.
    /// Default is 30 seconds.
    /// </summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Hop count limit for the broadcast/transit packet loop mitigation.
    /// Default is 8.
    /// </summary>
    public int Ttl { get; set; } = 8;

    /// <summary>
    /// Dictates whether to negotiate a dedicated RTC data subchannel for isolation (e.g. streaming or heavy loads).
    /// Default is false.
    /// </summary>
    public bool UseDedicatedChannel { get; set; } = false;
    public string NodeIdExclude { get; set; } = string.Empty;

    /// <summary>
    /// Cooperative cancellation token propagated down to async network tasks.
    /// </summary>
    public CancellationToken CancellationToken { get; set; } = default;
}

public class RouterResult
{
    public string? Payload { get; set; }
    public string? Error { get; set; }
    public bool IsSuccess => Error == null;
}


public interface IP2PRouterService
{
    /// <summary>
    /// Sends a request over a control or isolated WebRTC channel, awaiting its companion response payload.
    /// </summary>
    Task<RouterResult> SendRequestAsync(string targetNodeId, string commandType, string payloadJson, RouteSettings? settings = null);

    /// <summary>
    /// Initiates a progressive/segmented request, resetting sliding inactivity deadlines on each chunk delivery event.
    /// </summary>
    Task SendRequestProgressiveAsync(string targetNodeId, string commandType, string payloadJson, Action<string?, string?, double, bool> progressCallback, RouteSettings? settings = null);

    /// <summary>
    /// Floods a broadcast command down all active, handshaked and connected channels.
    /// </summary>
    Task FanoutBroadcastMessage(string commandType, string payloadJson, RouteSettings? settings = null);

    /// <summary>
    /// Sends a unidirectional, non-blocking fire-and-forget message targeting a remote node.
    /// </summary>
    Task SendMessage(string targetNodeId, string commandType, string payloadJson, RouteSettings? settings = null);

    /// <summary>
    /// Sends a unidirectional, non-blocking fire-and-forget message targeting a remote node.
    /// </summary>
    //Task SendMulticastMessage(string[] targetNodeIds, string commandType, string payloadJson, RouteSettings? settings = null);

    /// <summary>
    /// Streams intermediate response segments or progress update envelopes directly back to the requester.
    /// </summary>
    Task SendProgressiveResponseAsync(string targetNodeId, Guid requestId, string commandType, string payloadJson, double progressPercent, bool isCompleted, string errorMessage = "");

    /// <summary>
    /// 
    /// </summary>
    /// <param name="commandType"></param>
    /// <param name="handler">
    /// returns SenderNodeId, current packet, cancellation token with 15 minutes (for now) timeout for processing
    /// </param>
    void RegisterCommandHandler(string commandType, Func<string, P2PPacketEnvelope, CancellationToken, Task<string>> handler);
    void UnregisterCommandHandler(string commandType);
}

public class P2PRouterService : IP2PRouterService, IDisposable
{
    private readonly ILogger<P2PRouterService> _logger;
    private readonly IPeerConnectionManager _connectionManager;
    private readonly INodeIdentityService _identity;
    private readonly IPeerTracker _peerTracker;

    private class ProgressiveTransferContext
    {
        /// <summary>
        /// payload, error, progress, iscompleted
        /// </summary>
        public Action<string?, string?, double, bool> Callback { get; set; } = null!;
        public TimeSpan InactivityTimeout { get; set; }
        public Timer Timer { get; set; } = null!;
    }

    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<string>> _pendingRequests = new();
    private readonly ConcurrentDictionary<Guid, ProgressiveTransferContext> _progressiveTransfers = new();
    private readonly ConcurrentDictionary<string, Func<string, P2PPacketEnvelope, CancellationToken, Task<string>>> _commandHandlers = new();

    public P2PRouterService(
            ILogger<P2PRouterService> logger,
            IPeerConnectionManager connectionManager,
            INodeIdentityService identity,
            IPeerTracker peerTracker)
    {
        _connectionManager = connectionManager;
        _identity = identity;
        _logger = logger;
        _peerTracker = peerTracker;

        _connectionManager.OnRawMessageReceived += HandleIncomingRawPacket;
    }

    private void HandleIncomingRawPacket(string senderNodeId, string channelLabel, P2PPacketEnvelope fullEnvelope)
    {
        string myNodeId = _identity.GetLocalNodeId();

        if (string.IsNullOrEmpty(fullEnvelope.TargetNodeId) || fullEnvelope.TargetNodeId == myNodeId)
        {
            HandleTargetedEnvelopeAsync(senderNodeId, channelLabel, fullEnvelope);
            return;
        }

        // Forward relayed signals down adjacent connection routes
        if (fullEnvelope.CommandType == "RelaySignal" && fullEnvelope.Ttl > 1)
        {
            fullEnvelope.Ttl--;
            _ = SendEnvelopeDirectOrRelayedAsync(fullEnvelope.TargetNodeId, fullEnvelope);
        }
    }
    private async Task SendEnvelopeDirectOrRelayedAsync(string targetNodeId, P2PPacketEnvelope envelope)
    {
        var conn = _connectionManager.GetConnection(targetNodeId);
        if (conn != null && conn.PeerConnection != null && conn.PeerConnection.connectionState == SIPSorcery.Net.RTCPeerConnectionState.connected)
        {
            string serialized = JsonSerializer.Serialize(envelope);
            _connectionManager.SendMessageRaw(targetNodeId, serialized, "p2p-control-channel");
        }
        else
        {
            // Execute recursive DFS routing path derivation to avoid dirty flood-storms
            var visited = new HashSet<string> { _identity.GetLocalNodeId(), envelope.SenderNodeId };

            // Search if any of our active outbound peer gateways can reach the target node indirectly
            var optimalNextHopNodeId = FindNextHopRelayDfs(targetNodeId, visited);

            if (!string.IsNullOrEmpty(optimalNextHopNodeId))
            {
                _logger.LogInformation("[P2P Transport] Direct link down to {Target}. Routing envelope via calculated DFS relay gateway next-hop: {Hop}",
                    targetNodeId, optimalNextHopNodeId);

                string serialized = JsonSerializer.Serialize(envelope);
                _connectionManager.SendMessageRaw(optimalNextHopNodeId, serialized, "p2p-control-channel");
            }
            else
            {
                _logger.LogWarning("[P2P Transport] Drop packet: DFS routing tree found no connected relay path bridge to destination {Target}", targetNodeId);
            }
        }
        await Task.CompletedTask;
    }

    /// <summary>
    /// Recursive DFS routine tracing passive peer relationship boundaries via tracker records to locate a live route bridge.
    /// </summary>
    private string? FindNextHopRelayDfs(string targetNodeId, HashSet<string> visited)
    {
        // Fetch target node data structure from historical tracking bounds
        var targetPeer = _peerTracker.GetPeer(targetNodeId);
        if (targetPeer == null || targetPeer.LastSeenByNodeId == null || targetPeer.LastSeenByNodeId.Count == 0)
        {
            return null;
        }

        foreach (var potentialRelayId in targetPeer.LastSeenByNodeId)
        {
            if (string.IsNullOrEmpty(potentialRelayId) || !visited.Add(potentialRelayId))
            {
                continue; // Cycle detected or malformed string context, drop branch evaluation
            }

            // Base Case: If we have a fully live WebRTC socket session established with this witness, it is our next hop
            var activeConn = _connectionManager.GetConnection(potentialRelayId);
            if (activeConn != null && activeConn.PeerConnection != null
                && activeConn.PeerConnection.connectionState == SIPSorcery.Net.RTCPeerConnectionState.connected)
            {
                return potentialRelayId;
            }

            // Recursive Step: Look deeper into this potential relay's upstream witness chain
            var deepNextHop = FindNextHopRelayDfs(potentialRelayId, visited);
            if (!string.IsNullOrEmpty(deepNextHop))
            {
                // Unwind recursion: if this path is functional, return the node we have a direct connection with
                return potentialRelayId;
            }
        }

        return null;
    }

    //private async Task SendEnvelopeDirectOrRelayedAsync(string targetNodeId, P2PPacketEnvelope envelope)
    //{
    //    var conn = _connectionManager.GetActiveConnection(targetNodeId);
    //    if (conn != null && conn.PeerConnection != null && conn.PeerConnection.connectionState == SIPSorcery.Net.RTCPeerConnectionState.connected)
    //    {
    //        string serialized = JsonSerializer.Serialize(envelope);
    //        _connectionManager.SendMessageRaw(targetNodeId, serialized, "p2p-control-channel");
    //    }
    //    else
    //    {
    //        //TODO: redo
    //        var nextHops = _connectionManager.GetActiveConnections()
    //            .Where(c => c.ConnectionTypes.Contains("outbound"))
    //            .ToList();

    //        string serialized = JsonSerializer.Serialize(envelope);
    //        foreach (var hop in nextHops)
    //        {
    //            if (hop.NodeId != envelope.SenderNodeId && hop.NodeId != _identity.GetLocalNodeId())
    //            {
    //                _connectionManager.SendMessageRaw(hop.NodeId, serialized, "p2p-control-channel");
    //            }
    //        }
    //    }
    //}

    private void HandleTargetedEnvelopeAsync(string senderNodeId, string channelLabel, P2PPacketEnvelope frame)
    {
        if (frame.CommandType == "RelaySignal")
        {
            _connectionManager.HandleRelaySignaling(senderNodeId, frame);
            return;
        }

        // A. Standard single request promise match
        if (frame.CommandType.StartsWith("Response-", StringComparison.OrdinalIgnoreCase) && _pendingRequests.TryRemove(frame.MessageId, out var tcs))
        {
            if (!string.IsNullOrEmpty(frame.ErrorMessage)) tcs.TrySetException(new Exception(frame.ErrorMessage));
            else tcs.TrySetResult(frame.SerializedPayload);
            return;
        }

        // B. Segmented/Progressive callback match
        if (_progressiveTransfers.TryGetValue(frame.MessageId, out var context))
        {
            try
            {
                // Reset sliding inactivity window on chunk arrival
                context.Timer.Change(context.InactivityTimeout, Timeout.InfiniteTimeSpan);
            }
            catch (ObjectDisposedException) { }

            _ = Task.Run(() =>
            {
                try
                {
                    context.Callback(frame.SerializedPayload, frame.ErrorMessage, frame.ProgressPercent, frame.IsCompleted);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[P2P] Помилка виконання callback для MessageId: {Id}", frame.MessageId);
                }
            });

            if (frame.IsCompleted || !string.IsNullOrEmpty(frame.ErrorMessage))
            {
                if (_progressiveTransfers.TryRemove(frame.MessageId, out var completedContext))
                {
                    completedContext.Timer.Dispose();
                }
            }
            return;
        }

        // C. Standard Command routing
        if (_commandHandlers.TryGetValue(frame.CommandType, out var commandHandler))
        {
            Task.Run(async () =>
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(15));
                try
                {
                    string responsePayload = await commandHandler(frame.SenderNodeId, frame, cts.Token);

                    // Skip automatic final envelope write if progressive handler wrote custom segments
                    if (responsePayload == null) return;

                    var responseEnvelope = new P2PPacketEnvelope
                    {
                        MessageId = frame.MessageId,
                        SenderNodeId = _identity.GetLocalNodeId(),
                        TargetNodeId = frame.SenderNodeId,
                        CommandType = "Response-" + frame.CommandType,
                        SerializedPayload = responsePayload,
                        IsCompleted = true,
                        Ttl = 8,
                        CreatedUtc = DateTime.UtcNow
                    };
                    _connectionManager.SendMessageRaw(frame.SenderNodeId, JsonSerializer.Serialize(responseEnvelope), channelLabel);
                }
                catch (OperationCanceledException) { SendErrorResponse(frame.SenderNodeId, frame.MessageId, "Operation cancelled.", channelLabel); }
                catch (Exception ex) { SendErrorResponse(frame.SenderNodeId, frame.MessageId, ex.Message, channelLabel); }
            });
        }
    }

    public async Task FanoutBroadcastMessage(string commandType, string payloadJson, RouteSettings? settings = null)
    {
        var activeSettings = settings ?? new RouteSettings();
        var envelope = new P2PPacketEnvelope
        {
            MessageId = Guid.NewGuid(),
            SenderNodeId = _identity.GetLocalNodeId(),
            TargetNodeId = string.Empty,
            CommandType = commandType,
            SerializedPayload = payloadJson,
            Ttl = activeSettings.Ttl,
            CreatedUtc = DateTime.UtcNow
        };

        var activeConns = _connectionManager.GetActiveConnections();
        string serializedEnvelope = JsonSerializer.Serialize(envelope);

        foreach (var conn in activeConns)
        {
            if (conn.IsHandshaked && 
                conn.NodeId != settings?.NodeIdExclude &&
                conn.PeerConnection?.connectionState == SIPSorcery.Net.RTCPeerConnectionState.connected)
            {
                try
                {
                    if (activeSettings.CancellationToken.IsCancellationRequested) break;
                    _connectionManager.SendMessageRaw(conn.NodeId, serializedEnvelope);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[Router] Failed to fanout broadcast envelope to node: {NodeId}", conn.NodeId);
                }
            }
        }
    }

    public async Task SendMessage(string targetNodeId, string commandType, string payloadJson, RouteSettings? settings = null)
    {
        var activeSettings = settings ?? new RouteSettings();
        var envelope = new P2PPacketEnvelope
        {
            MessageId = Guid.NewGuid(),
            SenderNodeId = _identity.GetLocalNodeId(),
            TargetNodeId = targetNodeId,
            CommandType = commandType,
            SerializedPayload = payloadJson,
            Ttl = activeSettings.Ttl,
            CreatedUtc = DateTime.UtcNow
        };

        string serialized = JsonSerializer.Serialize(envelope);
        _connectionManager.SendMessageRaw(targetNodeId, serialized, "p2p-control-channel");
    }

    //todo
    //public async Task SendMulticastMessage(string[] targetNodeIds, string commandType, string payloadJson, RouteSettings? settings = null)
    //{
    //    var activeSettings = settings ?? new RouteSettings();
    //    var envelope = new P2PPacketEnvelope
    //    {
    //        MessageId = Guid.NewGuid(),
    //        SenderNodeId = _identity.GetLocalNodeId(),
    //        TargetNodeId = targetNodeId,
    //        CommandType = commandType,
    //        SerializedPayload = payloadJson,
    //        Ttl = activeSettings.Ttl,
    //        CreatedUtc = DateTime.UtcNow,

    //    };

    //    _connectionManager.GetActiveConnections();
    //    foreach(var connection )

    //    string serialized = JsonSerializer.Serialize(envelope);
    //    _connectionManager.SendMessageRaw(targetNodeId, serialized, "p2p-control-channel");
    //}

    public async Task SendProgressiveResponseAsync(string targetNodeId, Guid requestId, string commandType, string payloadJson, double progressPercent, bool isCompleted, string errorMessage = "")
    {
        var responseEnvelope = new P2PPacketEnvelope
        {
            MessageId = requestId,
            SenderNodeId = _identity.GetLocalNodeId(),
            TargetNodeId = targetNodeId,
            CommandType = "Response-" + commandType,
            SerializedPayload = payloadJson,
            ProgressPercent = progressPercent,
            IsCompleted = isCompleted,
            ErrorMessage = errorMessage,
            Ttl = 8,
            CreatedUtc = DateTime.UtcNow
        };

        _connectionManager.SendMessageRaw(targetNodeId, JsonSerializer.Serialize(responseEnvelope), "p2p-control-channel");
    }

    public async Task<RouterResult> SendRequestAsync(string targetNodeId, string commandType, string payloadJson, RouteSettings? settings = null)
    {
        var activeSettings = settings ?? new RouteSettings();
        var envelope = new P2PPacketEnvelope
        {
            MessageId = Guid.NewGuid(),
            SenderNodeId = _identity.GetLocalNodeId(),
            TargetNodeId = targetNodeId,
            CommandType = commandType,
            SerializedPayload = payloadJson,
            Ttl = activeSettings.Ttl,
            CreatedUtc = DateTime.UtcNow
        };

        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingRequests[envelope.MessageId] = tcs;

        string channelLabel = activeSettings.UseDedicatedChannel ? $"req-{envelope.MessageId:n}" : "p2p-control-channel";

        if (activeSettings.UseDedicatedChannel)
        {
            var tempChannel = await _connectionManager.CreateManagedDataChannelAsync(targetNodeId, channelLabel);
            if (tempChannel.IsError)
            {
                _pendingRequests.TryRemove(envelope.MessageId, out _);
                return new RouterResult { Error = $"Failed to open dedicated WebRTC sub-channel: {tempChannel.Error}" };
            }
        }

        try
        {
            string serialized = JsonSerializer.Serialize(envelope);
            bool isSent = _connectionManager.SendMessageRaw(targetNodeId, serialized, channelLabel);

            if (!isSent)
            {
                _pendingRequests.TryRemove(envelope.MessageId, out _);
                CleanupDedicatedChannelDeferred(targetNodeId, channelLabel, activeSettings.UseDedicatedChannel);
                return new RouterResult { Error = $"Target channel '{channelLabel}' is offline." };
            }
        }
        catch (Exception ex)
        {
            _pendingRequests.TryRemove(envelope.MessageId, out _);
            CleanupDedicatedChannelDeferred(targetNodeId, channelLabel, activeSettings.UseDedicatedChannel);
            return new RouterResult { Error = ex.Message };
        }

        using var timeoutCts = new CancellationTokenSource(activeSettings.Timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, activeSettings.CancellationToken);

        using (linkedCts.Token.Register(() => tcs.TrySetCanceled()))
        {
            try
            {
                string resultValue = await tcs.Task;
                CleanupDedicatedChannelDeferred(targetNodeId, channelLabel, activeSettings.UseDedicatedChannel);
                return new RouterResult { Payload = resultValue };
            }
            catch (TaskCanceledException)
            {
                _pendingRequests.TryRemove(envelope.MessageId, out _);
                CleanupDedicatedChannelDeferred(targetNodeId, channelLabel, activeSettings.UseDedicatedChannel);
                return new RouterResult { Error = activeSettings.CancellationToken.IsCancellationRequested ? "Cooperative cancellation triggered." : "Request timed out." };
            }
        }
    }

    public async Task SendRequestProgressiveAsync(
        string targetNodeId,
        string commandType,
        string payloadJson,

        Action<string?, string?, double, bool> progressCallback,
        RouteSettings? settings = null)
    {
        var activeSettings = settings ?? new RouteSettings();
        var envelope = new P2PPacketEnvelope
        {
            MessageId = Guid.NewGuid(),
            SenderNodeId = _identity.GetLocalNodeId(),
            TargetNodeId = targetNodeId,
            CommandType = commandType,
            SerializedPayload = payloadJson,
            Ttl = activeSettings.Ttl,
            CreatedUtc = DateTime.UtcNow
        };

        var context = new ProgressiveTransferContext
        {
            Callback = progressCallback,
            InactivityTimeout = activeSettings.Timeout
        };

        context.Timer = new Timer(state =>
        {
            var msgId = (Guid)state!;
            if (_progressiveTransfers.TryRemove(msgId, out var expiredContext))
            {
                expiredContext.Timer.Dispose();
                expiredContext.Callback(null, "TIMEOUT", 0, true);
            }
        }, envelope.MessageId, activeSettings.Timeout, Timeout.InfiniteTimeSpan);

        _progressiveTransfers[envelope.MessageId] = context;

        try
        {
            string serialized = JsonSerializer.Serialize(envelope);
            bool isSent = _connectionManager.SendMessageRaw(targetNodeId, serialized);
            if (!isSent)
            {
                if (_progressiveTransfers.TryRemove(envelope.MessageId, out var failedContext))
                {
                    failedContext.Timer.Dispose();
                }
                progressCallback(null, "OFFLINE", 0, true);
                return;
            }
        }
        catch (Exception ex)
        {
            if (_progressiveTransfers.TryRemove(envelope.MessageId, out var failedContext))
            {
                failedContext.Timer.Dispose();
            }
            progressCallback(null, ex.Message, 0, true);
            return;
        }

        if (activeSettings.CancellationToken != default)
        {
            activeSettings.CancellationToken.Register(() =>
            {
                if (_progressiveTransfers.TryRemove(envelope.MessageId, out var cancelledContext))
                {
                    cancelledContext.Timer.Dispose();
                    cancelledContext.Callback(null, "Cooperative cancellation triggered.", 0, true);
                }
            });
        }
    }

    public void RegisterCommandHandler(string commandType, Func<string, P2PPacketEnvelope, CancellationToken, Task<string>> handler) => _commandHandlers[commandType] = handler;
    public void UnregisterCommandHandler(string commandType) => _commandHandlers.TryRemove(commandType, out _);

    private void SendErrorResponse(string targetNodeId, Guid messageId, string errorReason, string channelLabel)
    {
        var errorFrame = new P2PPacketEnvelope { MessageId = messageId, SenderNodeId = _identity.GetLocalNodeId(), TargetNodeId = targetNodeId, CommandType = "Response-Error", ErrorMessage = errorReason, Ttl = 8, CreatedUtc = DateTime.UtcNow };
        _connectionManager.SendMessageRaw(targetNodeId, JsonSerializer.Serialize(errorFrame), channelLabel);
    }

    private void CleanupDedicatedChannelDeferred(string targetNodeId, string label, bool useDedicated)
    {
        if (!useDedicated) return;
        Task.Run((Func<Task?>)(async () =>
        {
            await Task.Delay(100);
            var conn = _connectionManager.GetConnection(targetNodeId);
            if (conn != null && conn.DataChannels.TryGetValue(label, out var dc)) { try { dc.close(); } catch { } }
        }));
    }

    public void Dispose()
    {
        _connectionManager.OnRawMessageReceived -= HandleIncomingRawPacket;
        foreach (var ctx in _progressiveTransfers.Values) ctx.Timer.Dispose();
        _progressiveTransfers.Clear();
    }
}