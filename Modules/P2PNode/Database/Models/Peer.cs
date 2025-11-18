using System;

using ProcessorApplication.Models.Nodes;

namespace ProcessorApplication.Sqlite.Models;

/**
 * peer is local node 
 * shared between other local peers
 */
public class Peer : NodeStored
{
    public int Id { get; set; }

    public void UpdateFrom(NodeWorking peerValue)
    {
        throw new NotImplementedException();
    }
}
