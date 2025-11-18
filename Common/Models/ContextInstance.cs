using System.Collections.Concurrent;

using Common.Interfaces.EventBus;

namespace Common.Models;
public class ContextInstance : ConcurrentDictionary<string, ICacheInstance>
{
    //public NodeCache Peers => GetOrCreate<NodeCache>("KnownPeers");
    //public NodeCache Trackers => GetOrCreate<NodeCache>("KnownTrackers");
    //public ConnectedNodes ConnectedNodes => GetOrCreate<ConnectedNodes>("HashedPeers");
    //public ConsensusList TrackerConsensus => GetOrCreate<ConsensusList>("TrackerConsensus");
    //public LocalReachabilityList LocalPeers => GetOrCreate<LocalReachabilityList>("LocalConsensus");
    //public LocalDataContext Config => GetOrCreate<LocalDataContext>("Config");
    //public DateTimeStore TimeStuff => GetOrCreate<DateTimeStore>("TimeCollection");

    private T GetOrCreate<T>(string key) where T : ICacheInstance, new()
    {
        return (T)(this.GetOrAdd(key, _ => new T()));
    }

    public ContextInstance()
    {
        // Pre-warm known keys
        //_ = Peers; _ = Trackers; _ = ConnectedNodes;
        //_ = TrackerConsensus; _ = LocalPeers;
        //_ = Config; _ = TimeStuff;
    }
}

//public class NodeContextInstance : ConcurrentDictionary<string, ICacheInstance>
//{
//    #pragma warning disable CS8602 // Dereference of a possibly null reference.
//    #pragma warning disable CS8603 // Possible null reference return.

//    //known nodes
//    public NodeCache Peers => this["KnownPeers"] as NodeCache;
//    public NodeCache Trackers => this["KnownTrackers"] as NodeCache;

//    //new nodes
//    //public NodeCache NewPeers => this["UpdatedPeers"] as NodeCache;
//    //public NodeCache NewTrackers => this["UpdatedTrackers"] as NodeCache;

//    public ConnectedNodes ConnectedNodes => this["HashedPeers"] as ConnectedNodes;

//    //tracker info
//    public ConsensusList TrackerConsensus => this["TrackerConsensus"] as ConsensusList;
//    //local peer info
//    public LocalReachabilityList LocalPeers => this["LocalConsensus"] as LocalReachabilityList;

//    //settigns config to share to actons
//    //should be updated from respective setting update listener
//    public LocalDataContext Config => this["Config"] as LocalDataContext;

//    public DateTimeStore TimeStuff => this["TimeCollection"] as DateTimeStore;

//#pragma warning restore CS8602 // Dereference of a possibly null reference.
//#pragma warning restore CS8603 // Possible null reference return.

//    public NodeContextInstance()
//    {
//        this["HashedPeers"] = new ConnectedNodes();

//        this["KnownPeers"] = new NodeCache();
//        //this["UpdatedPeers"] = new NodeCache();

//        this["KnownTrackers"] = new NodeCache();
//        //this["UpdatedTrackers"] = new NodeCache();

//        this["LocalConsensus"] = new ConsensusList();
//        this["TrackerConsensus"] = new LocalReachabilityList();

//        this["TimeCollection"] = new DateTimeStore();
//        this["Config"] = new LocalDataContext();
//    }
//}