using System.Collections.Concurrent;

using Common.Interfaces.EventBus;

namespace Common.Models.NodeContext;
/*
 * is generally here to store different datetimes
 **/
public class DateTimeStore : ConcurrentDictionary<string, DateTime>, ICacheInstance
{
    public DateTime LastGossip
    {
        get => this.TryGetValue("LastGossip", out var dt) ? dt : this["LastGossip"] = DateTime.MinValue;
        set => this["LastGossip"] = value;
    }
    public DateTime LastTrackerGossip
    {
        get => this.TryGetValue("LastTrackerGossip", out var dt) ? dt : this["LastTrackerGossip"] = DateTime.MinValue;
        set => this["LastTrackerGossip"] = value;
    }
    public DateTime LastPing
    {
        get => this.TryGetValue("LastPing", out var dt) ? dt : this["LastPing"] = DateTime.MinValue;
        set => this["LastPing"] = value;
    }
    public DateTime LastAdversise
    {
        get => this.TryGetValue("LastAdversise", out var dt) ? dt : this["LastAdversise"] = DateTime.MinValue;
        set => this["LastAdversise"] = value;
    }
}
