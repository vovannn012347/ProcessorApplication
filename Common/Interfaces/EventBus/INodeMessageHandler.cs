using System.Net;

using Common.Models;


namespace Common.Interfaces.EventBus;

public interface IMessageHandler
{
    string[] GetActions(); // e.g. ["PeerConnected", "ProcessingComplete"]
    Task HandleAsync(ActionMessage message, ContextInstance context, CancellationToken ct);
}

//public abstract class INodeMessageHandler
//{
//    public abstract string[] GetActions();
//    public abstract bool CanHandle(ActionMessage message);
//    public abstract Task HandleUnconnectedAsync(IPEndPoint sender, string action, string message);
//    public abstract Task HandlePeerAsync(NetPeer sender, string action, string message);
//}

//// the actual handler class that gets filtered and injected
//// this interface class is for reference ONLY
//public abstract class NodeMessageHandler : INodeMessageHandler
//{
//    public override sealed bool CanHandle(ActionMessage message) => GetActions().Contains(message.Action);
//}