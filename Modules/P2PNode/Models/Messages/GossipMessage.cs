using Common.Interfaces;

using ProcessorApplication.Models.Messages;
using ProcessorApplication.Models.Nodes;

namespace ProcessorApplication.Models;

public class GossipMessage : BaseMessage
{
    public const string MESSAGE_TYPE = "Gossip";
    public override string Action { get => MESSAGE_TYPE; set { } }


    public DateTime DataUpdated { get; set; } = DateTime.Now;
    public string HashKey { get; set; } = string.Empty;
    public string FriendlyName { get; set; } = string.Empty;
    //public int Port { get; set; }
    public int Load { get; set; }
    public bool IsTracker { get; set; }

    public List<NodeUpdate> Nodes { get; set; } = new List<NodeUpdate>();
}

public class NodeUpdate
{
    public DateTime DataUpdated { get; set; } = DateTime.Now;
    public string HashKey { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public int Port { get; set; }
    public string FriendlyName { get; set; } = string.Empty;
    public int Metric { get; set; }
    public bool IsTracker { get; set; } = false;

    internal static NodeUpdate FromNode(NodeWorking p)
    {
        return new NodeUpdate
        {
            HashKey = p.HashKey,
            Address = p.Address,
            Port = p.Port,
            FriendlyName = p.FriendlyName,
            Metric = p.Metric,
            IsTracker = p.Role
        };
    }
}