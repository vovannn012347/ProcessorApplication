using System.Text.Json;

namespace P2PModule.Services.Maintenance;

/// <summary>
/// One JSON line per consensus event in &lt;DataStoragePath&gt;/consensus_events.jsonl (node home: Module_Data/P2P), e.g.
/// {"t":"2026-11-24T02:10:03.512Z","node":"swarm-001","ev":"merge","session":"...","peer":"swarm-002","ok":true}
/// Events: start (role initiator|joined), merge (peer, ok), queue_empty, finish (reason, known).
/// Read by Build/swarm/swarm.py (check / analyze) to measure convergence across a swarm. Never throws.
/// </summary>
public static class ConsensusEventLog
{
    public const string FileName = "consensus_events.jsonl";
    private static readonly object Gate = new();

    /// <summary>Direct connections, one JSON line each in connection_events.jsonl: up (first answered ping on a
    /// connection, with rtt ms), rtt (every 15 s per peer at most), down (connection dropped).</summary>
    public const string ConnectionFileName = "connection_events.jsonl";

    public static void Write(string? dataStoragePath, string nodeId, string ev, Guid? sessionId, IDictionary<string, object?>? extra = null)
        => Append(dataStoragePath, FileName, nodeId, ev, sessionId, extra);

    public static void WriteConnection(string? dataStoragePath, string nodeId, string ev, string peer, int? rttMs = null, string? reason = null)
    {
        var extra = new Dictionary<string, object?> { ["peer"] = peer };
        if (rttMs != null) extra["rtt"] = rttMs;
        if (!string.IsNullOrEmpty(reason)) extra["reason"] = reason;
        Append(dataStoragePath, ConnectionFileName, nodeId, ev, null, extra);
    }

    /// <summary>Rating changes, one JSON line each in rating_events.jsonl: observer, peer, change, new score (for the reputation plot).</summary>
    public const string RatingFileName = "rating_events.jsonl";

    public static void WriteRating(string? dataStoragePath, string observer, string peer, double delta, double score)
        => Append(dataStoragePath, RatingFileName, observer, "rating", null,
            new Dictionary<string, object?> { ["peer"] = peer, ["delta"] = Math.Round(delta, 3), ["score"] = Math.Round(score, 3) });

    private static void Append(string? dataStoragePath, string fileName, string nodeId, string ev, Guid? sessionId, IDictionary<string, object?>? extra)
    {
        try
        {
            var line = new Dictionary<string, object?>
            {
                ["t"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                ["node"] = nodeId,
                ["ev"] = ev,
            };
            if (sessionId != null) line["session"] = sessionId.Value.ToString("N");
            if (extra != null)
            {
                foreach (var kv in extra) line[kv.Key] = kv.Value;
            }

            string dir = string.IsNullOrWhiteSpace(dataStoragePath) ? "./Module_Data/P2P" : dataStoragePath;
            Directory.CreateDirectory(dir);
            string json = JsonSerializer.Serialize(line);
            lock (Gate)
            {
                File.AppendAllText(Path.Combine(dir, fileName), json + Environment.NewLine);
            }
        }
        catch
        {
            // diagnostics only: never disturb the consensus itself
        }
    }
}

/// <summary>
/// Bytes and messages this node sent, by command family (consensus = commands starting with "Consensus"),
/// counted at the transport for the traffic columns of the swarm report.
/// </summary>
public static class TrafficCounter
{
    private static long _consensusBytes, _consensusMessages, _allBytes, _allMessages;
    public static long ConsensusBytes => Interlocked.Read(ref _consensusBytes);
    public static long ConsensusMessages => Interlocked.Read(ref _consensusMessages);
    public static long AllBytes => Interlocked.Read(ref _allBytes);
    public static long AllMessages => Interlocked.Read(ref _allMessages);

    public static void Count(string? payload)
    {
        if (string.IsNullOrEmpty(payload)) return;
        Interlocked.Add(ref _allBytes, payload.Length);
        Interlocked.Increment(ref _allMessages);
        int i = payload.IndexOf("\"CommandType\":\"", StringComparison.Ordinal);
        if (i >= 0 && string.CompareOrdinal(payload, i + 15, "Consensus", 0, 9) == 0)
        {
            Interlocked.Add(ref _consensusBytes, payload.Length);
            Interlocked.Increment(ref _consensusMessages);
        }
    }
}
