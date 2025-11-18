using LiteNetLib;

using Microsoft.Extensions.DependencyInjection;

namespace Common.Models;

public interface INodeAction
{
    string Name { get; }
    string GetPriority(); // e.g. "High", "Low"
    Task InitializeAsync(ContextInstance context, CancellationToken ct);
    Task ProcessAsync(
        CancellationToken ct,
        IServiceScope scope,
        ContextInstance context);
}

// Marker for DI
public interface IActionMarker { }


//public abstract class INodeAction
//{
//    //action is for setting priority
//    //priority is set in external list
//    public virtual string GetPriority() => string.Empty;

//    public abstract Task Process(
//        CancellationToken stoppingToken,
//        IServiceScope scope,
//        NetManager manager,
//        NodeContextInstance context);
//}

////the actual handler class that gets filtered and injected, interface class is for reference ONLY
//public abstract class NodeAction : INodeAction
//{

//}