using System;
using System.Collections.Concurrent;

using Common.Interfaces.EventBus;

using ProcessorApplication.Models.Nodes;

namespace Common.Models.NodeContext;
public class NodeCache : ConcurrentDictionary<string, NodeWorking>, ICacheInstance
{
    public NodeWorking this[string key] => GetOrAdd(key, _ => new NodeWorking());
}

public class ConnectedNodes : ConcurrentDictionary<int, ConnectionInfo>, ICacheInstance
{
    
}

