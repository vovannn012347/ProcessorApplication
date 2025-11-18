using System;

namespace ProcessorApplication.Models.Nodes;

/**
 * peer is local node 
 * shared between other local peers
 */
public class NodeStored
{
    public string Address { get; set; } = string.Empty;
    public int Port { get; set; } // typically 5000
    public string HashKey { get; set; } = string.Empty; // ECDSA-based hash, node identifier
    public string FriendlyName { get; set; } = string.Empty; // Human-readable name
    public DateTime LastSeen { get; set; }
    public DateTime FirstSeen { get; set; }

    // Routing metric (e.g., latency or bandwidth), sorting metric
    public double Metric { get; set; }
    // public int Load { get; set; } // Current load for load balancing(?)

    public NodeRole Role { get; set; }
    public ConnectionRole Connection { get; set; }
    public string NodeKnownFrom { get; set; } = string.Empty; //node with least latency that knows of this node
}
