using Common.Interfaces;

using ProcessorApplication.Models.Messages;

namespace ProcessorApplication.Models;

public class AdvertisePingMessage : BaseMessage
{
    public const string MESSAGE_TYPE = "Discovery";
    public override string Action { get => MESSAGE_TYPE; set { } }

    public string HashKey { get; set; } = string.Empty;
    public int Port { get; set; }
    public string FriendlyName { get; set; } = string.Empty;
    public int Load { get; set; }
    public bool IsTracker { get; set; }
}