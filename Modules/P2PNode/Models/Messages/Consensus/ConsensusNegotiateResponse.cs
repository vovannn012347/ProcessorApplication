using Common.Interfaces;

using ProcessorApplication.Models.Messages;

namespace ProcessorApplication.Models;

public class ConsensusNegotiateResponse : ActionMessage
{
    public const string MESSAGE_TYPE = "ConsensusNegotiateResponse";
    public override string Action => MESSAGE_TYPE;
    public string SenderHashKey { get; set; } = string.Empty;
}