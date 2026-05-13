using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace Infrastructure.Monitoring;

/// <summary>
/// A Singleton service that holds the "Last Seen" timestamps for all visitors.
/// </summary>
public class UserPresenceStore
{
    // Key: Unique User ID or Session ID, Value: Last Activity Time
    private readonly ConcurrentDictionary<string, DateTime> _lastSeen = new();

    public void RecordActivity(string identifier)
    {
        _lastSeen[identifier] = DateTime.UtcNow;
    }

    public int GetActiveCount(TimeSpan window)
    {
        var cutoff = DateTime.UtcNow - window;
        // Filter out anyone who hasn't made a request within the window (e.g. 5 mins)
        return _lastSeen.Values.Count(timestamp => timestamp > cutoff);
    }

    /// <summary>
    /// Optional: Background task can call this to keep memory clean
    /// </summary>
    public void PurgeExpired(TimeSpan timeout)
    {
        var cutoff = DateTime.UtcNow - timeout;
        foreach (var item in _lastSeen.Where(kvp => kvp.Value < cutoff))
        {
            _lastSeen.TryRemove(item.Key, out _);
        }
    }
}