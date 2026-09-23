// [full-implementation]
using System;

namespace P2PModule.Models.Network;

public class BootstrapConnectionResult
{
    public string NodeId { get; set; }
    public string Endpoint { get; set; }
    public List<string> Roles { get; set; }
}
public class P2PPacketEnvelope
{
    public Guid MessageId { get; set; } = Guid.NewGuid();
    public string SenderNodeId { get; set; } = string.Empty;
    public string TargetNodeId { get; set; } = string.Empty;
    public string CommandType { get; set; } = string.Empty;
    public string SerializedPayload { get; set; } = string.Empty;
    public string ErrorMessage { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    //multcast
    public string[]? TargetNodeIds { get; set; } = null;
    // Loop mitigation parameters
    public int Ttl { get; set; } = 8;
    //progressive
    public double ProgressPercent { get; set; }
    public bool IsCompleted { get; set; }
}