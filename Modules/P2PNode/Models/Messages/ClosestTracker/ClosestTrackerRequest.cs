using Common.Interfaces;

using ProcessorApplication.Models.Messages;

namespace ProcessorApplication.Models;

public class ClosestTrackerRequest : ActionMessage
{
    public const string MESSAGE_TYPE = "ClosestTrackerRequest";
    public override string Action => MESSAGE_TYPE;
    public string SenderHashKey { get; set; } = string.Empty;
    public string SenderAddress { get; set; } = string.Empty; // Sender's IP address
    public int SenderPort { get; set; }
    public int HopCount { get; set; } = 0; // Track redirects to prevent loops
}
