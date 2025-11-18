using System;

namespace ProcessorApplication.Models.Nodes;

/**
 * peer is local node 
 * shared between other local peers
 */

public class NodeWorking : NodeStored
{
    public DateTime LastPoked { get; set; }

    public int? NetPeerId { get; set; }
    public bool IsConnected => NetPeerId != null;

    public List<string> KnownNodes { get; set; } = new List<string>();

}
