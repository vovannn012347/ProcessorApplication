using Common.Interfaces;

using ProcessorApplication.Models.Messages;

namespace ProcessorApplication.Models;

public class ConsensusRequest : ActionMessage
{
    public const string MESSAGE_TYPE = "ConsensusRequest";
    public override string Action => MESSAGE_TYPE;
    public string SenderHashKey { get; set; } = string.Empty;
}