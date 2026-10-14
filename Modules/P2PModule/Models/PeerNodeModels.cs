using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;


using P2PModule.Infrastructure;
using P2PModule.Models;

using SIPSorcery.Net;

namespace P2PModule.Models;

public class PeerImmutableData
{
    public string NodeId { get; set; } = string.Empty;
    public byte[] PublicEncryptionKey { get; set; } = new byte[0];
    public byte[] PublicVerifyKey { get; set; } = new byte[0];
    //public byte[] PublicRefreshKey { get; set; } = new byte[0];

    //signature that verifies that our data exchanged is correct
    public List<string> Roles { get; set; } = new(); //self-reported roles
    //self-advertised signaling endpoints for faster connection
    public List<string> SignalingEndpoints { get; set; } = new();
    /// <summary>
    /// Ids of consensus channels (see Common.Interfaces.Consensus.IConsensusChannel) this node currently
    /// supports and is willing to run epidemic consensus on. Deliberately separate from Roles, which
    /// governs network-level access/promotion decisions (tracker, ...) and must not
    /// be repurposed for this.
    /// </summary>
    public List<string> ConsensusChannels { get; set; } = new();
    public DateTime SignatureTime { get; set; } = DateTime.MinValue;//actually date matters here
    public byte[] Signature { get; set; } = new byte[0];

    public byte[] GetNodeDataHash()
    {
        return SHA256.HashData(
            Convert.FromBase64String(P2PCrypto.CrCompliantStr(
                $"{NodeId}|{Convert.ToBase64String(PublicEncryptionKey)}|{string.Join(",", Roles)}|{string.Join(",", SignalingEndpoints)}|{string.Join(",", ConsensusChannels)}|{Convert.ToBase64String(PublicVerifyKey)}|{SignatureTime}")));
    }
}


public class SignatureEnvelope
{
    public string DayKey { get; set; } = string.Empty;
    public byte[] Nonce { get; set; } = Zero.Byte;
    public byte[] Signature { get; set; } = Zero.Byte;
}

public class PeerNodeTransferred
{
    public PeerImmutableData Identity { get; set; } = new();
    public List<SignatureEnvelope> Signatures { get; set; } = new();
    public int SignatureScore { get; set; } = 0;
    //public Dictionary<string, byte[]> Refresh { get; set; } = new();
    public double TrustScore { get; set; } = 0;

    public DateTime LastSeen { get; set; } = DateTime.MinValue;
}

public class PeerNodeHistoric : PeerNodeTransferred
{
    public PeerNodeHistoric()
    {
    }

    public PeerNodeHistoric(PeerNodeTransferred nodeFrom)
    {
        Identity = nodeFrom.Identity;
        LastSeen = nodeFrom.LastSeen;
        TrustScore = nodeFrom.TrustScore;
    }

    // Relay Gateway Tracking
    //so we are not lost when we are reloading
    //not really important for immediate save
    public HashSet<string> LastSeenByNodeId { get; set; } = new();
    //records are considered verified when they can correctly verify signatures in hash chain
    //or are correctly based upon previous 
    public bool IsVerified { get; set; } = false;
}

public class PeerNodeRuntime : PeerNodeHistoric
{
    public PeerNodeRuntime()
    {
    }

    public PeerNodeRuntime(PeerNodeTransferred nodeFrom) : base(nodeFrom)
    {
        Identity = nodeFrom.Identity;
        LastSeen = nodeFrom.LastSeen;
        TrustScore = nodeFrom.TrustScore;
    }

    public PeerNodeRuntime(PeerNodeHistoric nodeFrom) : base(nodeFrom)
    {
        LastSeenByNodeId = nodeFrom.LastSeenByNodeId;
        TrustScore = nodeFrom.TrustScore;
    }

    // Time Dilation Metric
    public long ClockOffsetMs { get; set; } = 0;

    //node transition signature updates
    //is obtained on handshake
    //if one hits in history - means node is what they say they are
    //public Dictionary<string, byte[]> NodeUpdateSignature { get; set; } = new();

    public void UpdateFrom(PeerNodeHistoric node)
    {
        LastSeen = node.LastSeen;
        //TrustScore = node.TrustScore;
    }
}

public class NodePersistentState
{
    public string NodeId { get; set; } = string.Empty;
    public HashSet<string> IndexEpochs { get; set; } = new();
    public string LastIndexEpoch { get; set; } = string.Empty;
    //persistent info
    public double TrustScore { get; set; } = 0;
    public DateTime LastUpdate { get; set; } = DateTime.MinValue;
}