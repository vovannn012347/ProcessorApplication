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