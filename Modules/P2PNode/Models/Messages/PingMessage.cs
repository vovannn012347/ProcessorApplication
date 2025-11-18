using Common.Interfaces;

using ProcessorApplication.Models.Messages;

namespace ProcessorApplication.Models;

public class PingMessage : BaseMessage
{
    public const string MESSAGE_TYPE = "Ping";
    public override string Action { get => MESSAGE_TYPE; set{} }
    public string HashKey { get; set; } = string.Empty;
}