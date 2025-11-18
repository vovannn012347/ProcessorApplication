using Common.Interfaces.EventBus;

namespace ProcessorApplication.Models.Messages;

public enum MessageType : byte
{
    Json = 1,
    Relay = 2,
    // room for extensions
}