using Common.Interfaces;

using ProcessorApplication.Models.Messages;
using ProcessorApplication.Models.Nodes;

namespace ProcessorApplication.Models;

public class HelloMessage : ActionMessage
{
    public const string MESSAGE_TYPE = "Hello";
    public override string Action { get => MESSAGE_TYPE; }

    public string HashKey { get; set; } = string.Empty;
    public string FriendlyName { get; set; } = string.Empty;
    public NodeRole Role { get; set; }
}

public class HiMessage : HelloMessage
{
    public new const string MESSAGE_TYPE = "Hi";
    public override string Action { get => MESSAGE_TYPE; }
}