using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using P2PModule.Configuration;
using P2PModule.Infrastructure;
using P2PModule.Interfaces;
using P2PModule.Models;
using P2PModule.Services.Connection;
using P2PModule.Services.Persistence;
using P2PModule.Models.Network;

namespace P2PModule.Services.Maintenance;

/// <summary>
/// Swarm test only: makes this node misbehave so the detection can be measured.
/// Section "EvilSettings" in the node's P2P settings, e.g. { "Attacks": ["slander", "equivocate"], "Rate": 1.0 }.
/// Ignored outside the NodeTrackerSwarm environment, whatever the settings say.
/// </summary>
public class EvilSettings
{
    /// <summary>slander, equivocate, forge, corrupt-delta, withhold, mitm</summary>
    public List<string> Attacks { get; set; } = new();
    /// <summary>Chance (0..1) that a given opportunity is used for the attack.</summary>
    public double Rate { get; set; } = 1.0;
    /// <summary>Coordinated attack: the other evil nodes. They are praised (trust 100) and spared: no withholding,
    /// forging or replaying against them.</summary>
    public List<string> Accomplices { get; set; } = new();
    /// <summary>Coordinated attack: the honest nodes every accomplice slanders and forges (empty = each node picks its own).</summary>
    public List<string> Victims { get; set; } = new();
}

/// <summary>Hook points for test attacks. The default does nothing.</summary>
public interface IEvilBehaviour
{
    bool Active { get; }
    /// <summary>Records this node is about to hand to another node (swap, pull): slander, equivocate, forge, withhold.</summary>
    List<PeerNodeTransferred> TamperOutgoingNodes(string toNodeId, List<PeerNodeTransferred> nodes);
    /// <summary>The signature chain of a delta this node announces: corrupt-delta.</summary>
    List<ChainSignature> TamperChain(List<ChainSignature> chain);
    /// <summary>A signed request someone sent us (start ping, merge init): mitm replays it elsewhere.</summary>
    void OnSignedRequest(string fromNodeId, string command, string payload);
}

public sealed class EvilBehaviour : IEvilBehaviour
{
    public const string SwarmEnvironment = "NodeTrackerSwarm";

    private readonly IOptionsMonitor<EvilSettings> _settings;
    private readonly IHostEnvironment _environment;
    private readonly INodeIdentityService _identity;
    private readonly IServiceProvider _services;
    private readonly IOptionsMonitor<P2PSettings> _p2p;
    private readonly ILogger<EvilBehaviour> _logger;
    private readonly object _lock = new();
    private DateTime _equivocationTime;
    private readonly HashSet<string> _victims = new(StringComparer.OrdinalIgnoreCase);
    private int _replays;
    private bool _announced;

    public EvilBehaviour(IOptionsMonitor<EvilSettings> settings, IHostEnvironment environment, INodeIdentityService identity,
        IServiceProvider services, IOptionsMonitor<P2PSettings> p2p, ILogger<EvilBehaviour> logger)
    {
        _settings = settings;
        _environment = environment;
        _identity = identity;
        _services = services;
        _p2p = p2p;
        _logger = logger;
    }

    public bool Active =>
        string.Equals(_environment.EnvironmentName, SwarmEnvironment, StringComparison.OrdinalIgnoreCase) &&
        (_settings.CurrentValue.Attacks?.Count ?? 0) > 0;

    private bool Has(string attack)
    {
        if (!Active) return false;
        if (!_settings.CurrentValue.Attacks.Any(a => string.Equals(a, attack, StringComparison.OrdinalIgnoreCase))) return false;
        if (!_announced)
        {
            _announced = true;
            _logger.LogWarning("[EVIL TEST] This node misbehaves on purpose (swarm test): {Attacks}, rate {Rate}{Coordinated}.",
                string.Join(", ", _settings.CurrentValue.Attacks), _settings.CurrentValue.Rate,
                _settings.CurrentValue.Accomplices?.Count > 0 ? $", coordinated with {string.Join(", ", _settings.CurrentValue.Accomplices)}" : "");
        }
        return Random.Shared.NextDouble() < Math.Clamp(_settings.CurrentValue.Rate, 0, 1);
    }

    private void Log(string attack, string? target, string detail)
    {
        _logger.LogWarning("[EVIL TEST] {Attack} -> {Target}: {Detail}", attack, target ?? "-", detail);
        ConsensusEventLog.Write(_p2p.CurrentValue.DataStoragePath, _identity.NodeId, "evil", null,
            new Dictionary<string, object?> { ["attack"] = attack, ["peer"] = target, ["detail"] = detail });
    }

    public List<PeerNodeTransferred> TamperOutgoingNodes(string toNodeId, List<PeerNodeTransferred> nodes)
    {
        if (!Active) return nodes;

        var me = _identity.NodeId;
        var cfg = _settings.CurrentValue;
        var accomplices = new HashSet<string>(cfg.Accomplices ?? new(), StringComparer.OrdinalIgnoreCase);
        bool toAccomplice = accomplices.Contains(toNodeId);

        // One record attack per opportunity, picked at random among this node's attacks. (They used to run one after
        // another, and withhold - which returns nothing - hid every other attack: a node given withhold + slander at
        // rate 1 never slandered.)
        var options = new[] { "withhold", "slander", "equivocate", "forge" }
            .Where(a => cfg.Attacks.Any(x => string.Equals(x, a, StringComparison.OrdinalIgnoreCase)))
            .Where(a => !(toAccomplice && (a == "withhold" || a == "forge")))   // accomplices are spared
            .ToList();
        if (options.Count == 0) return nodes;
        string pick = options[Random.Shared.Next(options.Count)];
        if (!Has(pick)) return nodes;

        if (pick == "withhold")
        {
            Log("withhold", toNodeId, $"sent nothing instead of {nodes.Count} record(s)");
            return new List<PeerNodeTransferred>();
        }

        if (pick == "slander")
        {
            // Push a fixed third of the others down (coordinated: the shared victims), ourselves and accomplices up
            lock (_lock)
            {
                if (_victims.Count == 0)
                {
                    if (cfg.Victims?.Count > 0) foreach (var v in cfg.Victims) _victims.Add(v);
                    else
                        foreach (var n in nodes.Where(n => n.Identity?.NodeId != null && !n.Identity.NodeId.Equals(me, StringComparison.OrdinalIgnoreCase)
                                                           && !accomplices.Contains(n.Identity.NodeId)))
                            if (Random.Shared.Next(3) == 0) _victims.Add(n.Identity.NodeId);
                }
            }
            int down = 0, up = 0;
            foreach (var n in nodes)
            {
                if (n.Identity?.NodeId == null) continue;
                if (_victims.Contains(n.Identity.NodeId)) { n.TrustScore = -50; down++; }
                else if (n.Identity.NodeId.Equals(me, StringComparison.OrdinalIgnoreCase)) n.TrustScore = 100;
                else if (accomplices.Contains(n.Identity.NodeId)) { n.TrustScore = 100; up++; }
            }
            if (down > 0 || up > 0) Log("slander", toNodeId, $"reported {down} node(s) at trust -50" + (up > 0 ? $", {up} accomplice(s) at 100" : ""));
        }

        if (pick == "equivocate")
        {
            // Two different self-records with the same signature time, one or the other depending on the receiver
            lock (_lock) { if (_equivocationTime == default) _equivocationTime = DateTime.UtcNow; }
            var keys = _identity.GetDayKeys();
            var self = _identity.GetImmutableData();
            self.SignatureTime = _equivocationTime;
            bool variantB = (StringComparer.OrdinalIgnoreCase.GetHashCode(toNodeId) & 1) == 1;
            if (variantB) self.SignalingEndpoints = self.SignalingEndpoints.Concat(new[] { "equivocation.invalid:1" }).ToList();
            self.Signature = P2PCrypto.SignData(self.GetNodeDataHash(), keys.PrivateSignatureKey);
            nodes.RemoveAll(n => n.Identity?.NodeId != null && n.Identity.NodeId.Equals(me, StringComparison.OrdinalIgnoreCase));
            nodes.Add(new PeerNodeTransferred { Identity = self, TrustScore = 100, LastSeen = DateTime.UtcNow });
            Log("equivocate", toNodeId, $"self-record variant {(variantB ? "B" : "A")} at {_equivocationTime:O}");
        }

        if (pick == "forge")
        {
            // Claim another node's id with our own keys (coordinated: one of the shared victims; never an accomplice)
            var candidates = nodes.Where(n => n.Identity?.NodeId != null && !n.Identity.NodeId.Equals(me, StringComparison.OrdinalIgnoreCase)
                                              && !accomplices.Contains(n.Identity.NodeId)).ToList();
            var shared = candidates.Where(n => cfg.Victims?.Contains(n.Identity.NodeId, StringComparer.OrdinalIgnoreCase) == true).ToList();
            var victim = (shared.Count > 0 ? shared : candidates).OrderBy(_ => Random.Shared.Next()).FirstOrDefault();
            if (victim != null)
            {
                var keys = _identity.GetDayKeys();
                var forged = new PeerImmutableData
                {
                    NodeId = victim.Identity.NodeId,
                    PublicEncryptionKey = keys.PublicKey,
                    PublicVerifyKey = keys.PublicVerifyKey,
                    Roles = victim.Identity.Roles.ToList(),
                    SignalingEndpoints = victim.Identity.SignalingEndpoints.ToList(),
                    ConsensusChannels = victim.Identity.ConsensusChannels.ToList(),
                    SignatureTime = DateTime.UtcNow,
                };
                forged.Signature = P2PCrypto.SignData(forged.GetNodeDataHash(), keys.PrivateSignatureKey);
                victim.Identity = forged;
                victim.Signatures = new List<SignatureEnvelope>();
                Log("forge", toNodeId, $"forged record of {forged.NodeId} on our own key");
            }
        }

        return nodes;
    }

    public List<ChainSignature> TamperChain(List<ChainSignature> chain)
    {
        if (!Active || chain.Count == 0 || !Has("corrupt-delta")) return chain;
        var me = _identity.NodeId;
        var copy = chain.Select(c => new ChainSignature
        {
            Order = c.Order, SignerNodeId = c.SignerNodeId, TargetNodeId = c.TargetNodeId,
            TimestampUtc = c.TimestampUtc, Signature = c.Signature.ToArray(),
        }).ToList();
        var link = copy.Where(c => !c.SignerNodeId.Equals(me, StringComparison.OrdinalIgnoreCase) && c.Signature.Length > 8)
            .OrderBy(_ => Random.Shared.Next()).FirstOrDefault()
            ?? copy.FirstOrDefault(c => c.Signature.Length > 8);
        if (link == null) return chain;
        link.Signature[link.Signature.Length / 2] ^= 0x5A;
        Log("corrupt-delta", null, $"flipped a byte of chain link {link.Order} {link.SignerNodeId}->{link.TargetNodeId}");
        return copy;
    }

    public void OnSignedRequest(string fromNodeId, string command, string payload)
    {
        if (!Active || !Has("mitm")) return;
        if (Interlocked.Increment(ref _replays) > 20) return; // enough to be seen, not a flood
        var connections = _services.GetRequiredService<IPeerConnectionManager>();
        var router = _services.GetRequiredService<IP2PRouterService>();
        var me = _identity.NodeId;
        var accomplices = new HashSet<string>(_settings.CurrentValue.Accomplices ?? new(), StringComparer.OrdinalIgnoreCase);
        var target = connections.GetActiveConnections()
            .Where(c => c?.NodeId != null && !c.NodeId.Equals(fromNodeId, StringComparison.OrdinalIgnoreCase) && !c.NodeId.Equals(me, StringComparison.OrdinalIgnoreCase)
                        && !accomplices.Contains(c.NodeId)
                        && c.PeerConnection?.connectionState == SIPSorcery.Net.RTCPeerConnectionState.connected)
            .Select(c => c.NodeId).OrderBy(_ => Random.Shared.Next()).FirstOrDefault();
        if (target == null) return;
        Log("mitm", target, $"replaying {command} signed by {fromNodeId}");
        _ = Task.Run(async () =>
        {
            try { await router.SendRequestAsync(target, command, payload, new RouteSettings { Timeout = TimeSpan.FromSeconds(10) }); }
            catch { }
        });
    }
}
