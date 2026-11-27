using System.Security.Cryptography;
using System.Text.Json;

using Microsoft.Extensions.Logging;

using P2PModule.Models;

namespace P2PModule.Services.Maintenance;

/// <summary>
/// What a consensus session has been told about each node, and by whom.
///
/// Policy: a node we have not seen before is taken in (its self-signature must hold). One source alone
/// leaves it under doubt - the opinion fusion only firms up as further, independent sources report the
/// same record. Nobody is penalized for reporting something new. Penalties come only from a discrepancy:
/// two records of the same node that cannot both be true. The records involved are kept as evidence, so
/// they can be passed on by gossip later.
/// </summary>
public sealed class ConsensusObservationLedger
{
    private readonly object _lock = new();
    private readonly Dictionary<string, List<ObservedVersion>> _byNode = new(StringComparer.OrdinalIgnoreCase);
    // Sources counted during the current iteration: a rolled-back iteration takes them back out
    private readonly List<(string NodeId, string Fingerprint, string Source)> _pending = new();
    private readonly HashSet<string> _settled = new(StringComparer.OrdinalIgnoreCase);

    public ObservationResult Observe(string nodeId, PeerImmutableData identity, byte[] nodeHash, string source, bool lineageVerified)
    {
        string fingerprint = Fingerprint(nodeHash, identity.Signature);
        source ??= string.Empty;

        lock (_lock)
        {
            if (!_byNode.TryGetValue(nodeId, out var versions))
                _byNode[nodeId] = versions = new List<ObservedVersion>();

            var version = versions.FirstOrDefault(v => v.Fingerprint == fingerprint);
            if (version == null)
            {
                version = new ObservedVersion { Fingerprint = fingerprint, Identity = identity };
                versions.Add(version);
            }
            version.LineageVerified |= lineageVerified;

            bool newSource = version.Sources.Add(source);
            if (newSource) _pending.Add((nodeId, fingerprint, source));

            var conflict = ObservationConflict.None;
            var rivals = new List<ObservedVersion>();
            foreach (var other in versions)
            {
                if (ReferenceEquals(other, version)) continue;
                bool sameKey = other.Identity.PublicVerifyKey.AsSpan().SequenceEqual(identity.PublicVerifyKey);

                if (sameKey && other.Identity.SignatureTime == identity.SignatureTime)
                {
                    // One key signed two different records for the same moment: the key holder did both
                    conflict = ObservationConflict.Equivocation;
                    rivals.Add(other);
                }
                else if (!sameKey && other.Identity.SignatureTime.Date == identity.SignatureTime.Date)
                {
                    // Two different keys claim this node for the same day: one of them is not the node
                    if (conflict == ObservationConflict.None) conflict = ObservationConflict.ContestedKey;
                    rivals.Add(other);
                }
            }

            return new ObservationResult(version, newSource, conflict, rivals);
        }
    }

    /// <summary>For a contested node: the record we go with. Lineage to an earlier day's key wins; otherwise the one more independent sources report.</summary>
    public ObservedVersion? Leader(IEnumerable<ObservedVersion> contenders)
    {
        lock (_lock)
        {
            var ordered = contenders
                .OrderByDescending(v => v.LineageVerified)
                .ThenByDescending(v => v.Sources.Count)
                .ToList();
            if (ordered.Count == 0) return null;
            if (ordered.Count > 1 &&
                ordered[0].LineageVerified == ordered[1].LineageVerified &&
                ordered[0].Sources.Count == ordered[1].Sources.Count)
            {
                return null; // a tie: nothing decided yet
            }
            return ordered[0];
        }
    }

    /// <summary>Every verify key seen claiming this node (any version, any source).</summary>
    public List<byte[]> KeysFor(string nodeId)
    {
        lock (_lock)
        {
            var keys = new List<byte[]>();
            if (_byNode.TryGetValue(nodeId, out var versions))
                foreach (var v in versions)
                    if (v.Identity.PublicVerifyKey?.Length > 0 && !keys.Any(k => k.AsSpan().SequenceEqual(v.Identity.PublicVerifyKey)))
                        keys.Add(v.Identity.PublicVerifyKey);
            return keys;
        }
    }

    /// <summary>More than one key claims this node today: which one is really its key is not settled.</summary>
    public bool IsContested(string nodeId) => KeysFor(nodeId).Count > 1;

    /// <summary>
    /// Nodes (other than <paramref name="exceptNodeId"/>) whose OWN record carries this verify key. A record of
    /// node X signed with that key was made by that node: the key is its daily key. That is who forged X - not the
    /// nodes that merely passed the record on.
    /// </summary>
    public List<string> OwnersOfKey(byte[] key, string exceptNodeId)
    {
        lock (_lock)
        {
            return _byNode
                .Where(kv => !kv.Key.Equals(exceptNodeId, StringComparison.OrdinalIgnoreCase) &&
                             kv.Value.Any(v => v.Identity.PublicVerifyKey.AsSpan().SequenceEqual(key)))
                .Select(kv => kv.Key).ToList();
        }
    }

    /// <summary>True the first time a given conflict/party pair is settled (so a penalty is applied once).</summary>
    public bool Settle(string key)
    {
        lock (_lock) return _settled.Add(key);
    }

    public void CommitIteration()
    {
        lock (_lock) _pending.Clear();
    }

    public void RollbackIteration()
    {
        lock (_lock)
        {
            foreach (var (nodeId, fingerprint, source) in _pending)
            {
                if (!_byNode.TryGetValue(nodeId, out var versions)) continue;
                var version = versions.FirstOrDefault(v => v.Fingerprint == fingerprint);
                if (version == null) continue;
                version.Sources.Remove(source);
                if (version.Sources.Count == 0) versions.Remove(version);
            }
            _pending.Clear();
        }
    }

    // nodeId -> source -> reported trust score; source -> subjects it was an outlier on
    private readonly Dictionary<string, Dictionary<string, double>> _trust = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> _outliers = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Records a source's trust score for a node. Once at least two OTHER sources reported the same node, a source
    /// further than <paramref name="threshold"/> from their median is an outlier on that node. Returns the sources
    /// that became outliers on a new subject, with how many subjects they are outliers on now.
    /// </summary>
    private readonly Dictionary<string, HashSet<string>> _compared = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _flagged = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Records a source's trust score for a node and returns the sources that have just crossed the slander line:
    /// - on a node at least two OTHER sources reported (with an opinion, i.e. not 0), it is further than max(threshold = 50, 3 x the others' median
    ///   absolute deviation) from their median (honest nodes disagree a little: local penalties, decay, timing);
    /// - and that happened on at least 3 nodes AND on at least 30 % of the nodes it could be compared on.
    /// Each source is returned once per session.
    /// </summary>
    public List<(string Source, int Subjects, double Value, double Median)> ObserveTrust(string nodeId, string source, double trust, double threshold = 50)
    {
        var result = new List<(string, int, double, double)>();
        if (string.IsNullOrEmpty(source)) return result;
        // 0 is the score of a record nobody has rated yet ("no opinion"), not a claim: honest 20-node runs had
        // 0 next to medians of ~40 and were reported as slander
        if (trust == 0) return result;
        lock (_lock)
        {
            if (!_trust.TryGetValue(nodeId, out var bySource)) _trust[nodeId] = bySource = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            bySource[source] = trust;
            if (bySource.Count < 3) return result;
            foreach (var (src, value) in bySource)
            {
                var others = bySource.Where(kv => !kv.Key.Equals(src, StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Value).OrderBy(v => v).ToList();
                double median = Median(others);
                double mad = Median(others.Select(v => Math.Abs(v - median)).OrderBy(v => v).ToList());
                if (!_compared.TryGetValue(src, out var compared)) _compared[src] = compared = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                compared.Add(nodeId);
                if (Math.Abs(value - median) <= Math.Max(threshold, 3 * mad)) continue;
                if (!_outliers.TryGetValue(src, out var subjects)) _outliers[src] = subjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                subjects.Add(nodeId);
                if (subjects.Count >= 3 && subjects.Count >= 0.3 * compared.Count && _flagged.Add(src))
                    result.Add((src, subjects.Count, value, median));
            }
        }
        return result;
    }

    private static double Median(List<double> sorted) =>
        sorted.Count == 0 ? 0 : sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2;

    /// <summary>Nodes reported by exactly one other node so far, with that source.</summary>
    public List<(string NodeId, string Source)> SingleSource()
    {
        lock (_lock)
        {
            var list = new List<(string, string)>();
            foreach (var (nodeId, versions) in _byNode)
            {
                var sources = versions.SelectMany(v => v.Sources).Where(s => !string.IsNullOrEmpty(s)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (sources.Count == 1) list.Add((nodeId, sources[0]));
            }
            return list;
        }
    }

    /// <summary>Nodes reported this session: by one source only (taken in, still doubted) and by several.</summary>
    public (int SingleSource, int Corroborated) Summary()
    {
        lock (_lock)
        {
            int single = 0, multi = 0;
            foreach (var versions in _byNode.Values)
            {
                int sources = versions.SelectMany(v => v.Sources).Where(s => !string.IsNullOrEmpty(s)).Distinct(StringComparer.OrdinalIgnoreCase).Count();
                if (sources <= 1) single++; else multi++;
            }
            return (single, multi);
        }
    }

    private static string Fingerprint(byte[] nodeHash, byte[] signature)
    {
        var buffer = new byte[nodeHash.Length + (signature?.Length ?? 0)];
        Buffer.BlockCopy(nodeHash, 0, buffer, 0, nodeHash.Length);
        if (signature != null) Buffer.BlockCopy(signature, 0, buffer, nodeHash.Length, signature.Length);
        return Convert.ToHexString(SHA256.HashData(buffer)).Substring(0, 16);
    }
}

public enum ObservationConflict
{
    None,
    /// <summary>The same key signed two different records with the same signature time. Proof against the key holder.</summary>
    Equivocation,
    /// <summary>Different keys claim the same node id for the same day. Settled by lineage, then by the number of sources.</summary>
    ContestedKey,
}

public sealed class ObservedVersion
{
    public string Fingerprint { get; set; } = string.Empty;
    public PeerImmutableData Identity { get; set; } = new();
    public HashSet<string> Sources { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool LineageVerified { get; set; }
}

public sealed record ObservationResult(ObservedVersion Version, bool NewSource, ObservationConflict Conflict, List<ObservedVersion> Rivals);

/// <summary>
/// A discrepancy and the records behind it, kept on disk (consensus_evidence/&lt;date&gt;/) so it can be checked
/// later and passed on by gossip. Every record in it is self-signed (or a signed chain link), so a receiver can
/// verify it without trusting the node that detected it.
/// </summary>
public sealed class ConsensusEvidence
{
    public string Kind { get; set; } = string.Empty;
    public string SubjectNodeId { get; set; } = string.Empty;
    public string ConsensusDate { get; set; } = string.Empty;
    public string DetectedByNodeId { get; set; } = string.Empty;
    public DateTime DetectedUtc { get; set; } = DateTime.UtcNow;
    public string Reason { get; set; } = string.Empty;
    public List<EvidenceVersion> Versions { get; set; } = new();
    /// <summary>Signature chain links involved (for a delta whose chain does not verify).</summary>
    public object? Chain { get; set; }
    /// <summary>The signed request involved (start ping, merge init).</summary>
    public object? Request { get; set; }
    /// <summary>Anything else the check used (reported values, medians...).</summary>
    public object? Details { get; set; }
    public List<string> PenalizedNodeIds { get; set; } = new();
    /// <summary>Not yet handed to gossip.</summary>
    public bool Gossiped { get; set; }
}

public sealed class EvidenceVersion
{
    public PeerImmutableData Identity { get; set; } = new();
    public List<string> Sources { get; set; } = new();
    public bool LineageVerified { get; set; }
}

public static class ConsensusEvidenceStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static string? Write(string evidenceDir, ConsensusEvidence evidence, ILogger logger)
    {
        if (string.IsNullOrEmpty(evidenceDir)) return null;
        try
        {
            Directory.CreateDirectory(evidenceDir);
            string tag = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
                evidence.Kind + "|" + evidence.SubjectNodeId + "|" + string.Join(",", evidence.Versions.Select(v => Convert.ToBase64String(v.Identity.Signature)).OrderBy(s => s)) +
                "|" + (evidence.Request != null ? JsonSerializer.Serialize(evidence.Request) : string.Empty)))).Substring(0, 12);
            string path = Path.Combine(evidenceDir, $"{evidence.Kind}_{evidence.SubjectNodeId}_{tag}.json");
            // Same discrepancy -> same file: a later sighting refreshes the sources and keeps who was penalized before
            bool first = !File.Exists(path);
            if (first)
            {
                // <data>/consensus_evidence/<date>: one line in the node's consensus events, for analyze and the replay
                string? dataDir = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(evidenceDir)));
                ConsensusEventLog.Write(dataDir, evidence.DetectedByNodeId, "evidence", null, new Dictionary<string, object?>
                {
                    ["kind"] = evidence.Kind, ["peer"] = evidence.SubjectNodeId, ["penalized"] = evidence.PenalizedNodeIds,
                });
            }
            if (File.Exists(path))
            {
                try
                {
                    var earlier = JsonSerializer.Deserialize<ConsensusEvidence>(File.ReadAllText(path));
                    if (earlier != null)
                    {
                        evidence.PenalizedNodeIds = earlier.PenalizedNodeIds.Concat(evidence.PenalizedNodeIds).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                        evidence.DetectedUtc = earlier.DetectedUtc;
                    }
                }
                catch { }
            }
            File.WriteAllText(path, JsonSerializer.Serialize(evidence, Options));
            return path;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[Consensus Evidence] Could not save evidence about {NodeId}.", evidence.SubjectNodeId);
            return null;
        }
    }

    public static EvidenceVersion From(ObservedVersion v) => new()
    {
        Identity = v.Identity,
        Sources = v.Sources.Where(s => !string.IsNullOrEmpty(s)).OrderBy(s => s).ToList(),
        LineageVerified = v.LineageVerified,
    };
}
