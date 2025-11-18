using Common.Interfaces;

using ProcessorApplication.Models.Messages;
using ProcessorApplication.Models.Nodes;

namespace ProcessorApplication.Models;

public class ConsensusResponse : ActionMessage
{
    public const string MESSAGE_TYPE = "ConsensusResponse";
    public override string Action => MESSAGE_TYPE;
    public List<NodeWorking> Trackers { get; set; } = new List<NodeWorking>();
    public List<NodeWorking> Peers { get; set; } = new List<NodeWorking>();
    public string Signature { get; set; } = string.Empty; // Placeholder for crypto
}