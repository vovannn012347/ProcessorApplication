namespace Common.Interfaces.EventBus;
public interface IMessage { }

public abstract class BaseMessage : IMessage { }

public abstract class ActionMessage : BaseMessage
{
    public abstract string Action { get; }
}

public sealed class PeekActionMessage : ActionMessage
{
    public override string Action { get; } = string.Empty;
}