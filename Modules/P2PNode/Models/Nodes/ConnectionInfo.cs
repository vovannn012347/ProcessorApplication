using System;

namespace ProcessorApplication.Models.Nodes;
public class ConnectionInfo
{
    public string HashKey { get; set; } = string.Empty;
    public ConnectionRole Connection { get; set; }
    public NodeRole Role { get; set; }
    public int? NetPeerId { get; set; }
    public DateTime LastUpdated { get; set; }
}
