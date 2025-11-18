using Common.Interfaces;

using ProcessorApplication.Models.Messages;

namespace ProcessorApplication.Models;

public class ClosestTrackerResponse : ActionMessage
{
    public const string MESSAGE_TYPE = "ClosestTrackerResponse";
    public override string Action => MESSAGE_TYPE;
    public string TrackerHashKey { get; set; } = string.Empty;
    public string TrackerAddress { get; set; } = string.Empty;
    public int TrackerPort { get; set; }
    public bool IsClosest { get; set; } // True if this tracker is the closest
}
