using System.Collections.Concurrent;

using SIPSorcery.Net;

namespace P2PModule.Models;


/// <summary>
/// Representation of an active physical data tunnel containing native SIPSorcery WebRTC channel handles.
/// Consolidated connection metadata tracking handshake validation and low-latency relative clock dilation.
/// </summary>
public class ActivePeerConnection
{
    public string NodeId { get; set; } = string.Empty;
    public DateTime ConnectedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastActivity { get; set; } = DateTime.MinValue;
    public HashSet<string> ConnectionTypes { get; set; } = new HashSet<string>();

    // Handshake & Connectivity Status Info
    public bool IsHandshaked { get; set; } = false;
    public DateTime? HandshakedAtUtc { get; set; }

    // Time Dilation: Thread-safe circular queue holding exactly the last 4 clock offset samples
    public ConcurrentQueue<long> ClockOffsets { get; set; } = new ConcurrentQueue<long>();

    // Cached, hot-readable clock offset property to avoid average re-computation overhead on message routing
    public long ClockOffsetMs { get; set; } = 0;

    /// <summary>
    /// Native long-lived WebRTC Peer Connection session.
    /// </summary>
    public RTCPeerConnection PeerConnection { get; set; } = null!;

    /// <summary>
    /// Thread-safe map of active data channels indexed by their custom labels.
    /// </summary>
    public ConcurrentDictionary<string, RTCDataChannel> DataChannels { get; set; } = new ConcurrentDictionary<string, RTCDataChannel>();

    /// <summary>
    /// Enqueues a fresh relative offset sample, prunes the sliding queue size to 4,
    /// and recalculates the consolidated average value stored inside the ClockOffsetMs property.
    /// </summary>
    public void UpdateOffset(long newOffset)
    {
        ClockOffsets.Enqueue(newOffset);
        while (ClockOffsets.Count > 4)
        {
            ClockOffsets.TryDequeue(out _);
        }
        ClockOffsetMs = ClockOffsets.Count > 0 ? (long)ClockOffsets.Average() : 0;
    }
}

public class TimeSyncPacket
{
    public long SenderSendUtc { get; set; }
}

public class RelaySignalPayload
{
    public string OriginNodeId { get; set; } = string.Empty;
    public string TargetNodeId { get; set; } = string.Empty;
    public string Sdp { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public Guid HandShakeId { get; set; }
}

public class WebRTCSignalPayload
{
    public string SenderNodeId { get; set; } = string.Empty;
    public string Sdp { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public Guid HandShakeId { get; set; }
}