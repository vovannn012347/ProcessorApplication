using System;

namespace ProcessorApplication.Models.Nodes;

public enum NodeRole
{
    Unknown, //not introduced self yet
    Peer,
    PeerPromoted,
    Tracker,
    ConsensusTracker
}
