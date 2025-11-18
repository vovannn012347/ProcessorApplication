using System;

namespace ProcessorApplication.Models.Nodes;

public enum ConnectionRole
{
    Unknown,
    NotConnected,
    Outbound,
    Inbound,
    Mutual,
    Forced
}
