using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

using P2PModule.Infrastructure;
using P2PModule.Models.Gossips;

namespace P2PModule.Models;

public class NodesTransferContainer
{
    // The active frozen peer inventory configuration metrics array
    public List<PeerNodeTransferred> Nodes { get; set; } = new();
}

public class ConsensusSignatureResponse
{
    public byte[] Signature { get; set; } = new byte[0];
}

public class ConsensusDeltaPing
{
    /// <summary>
    /// NodeId of node we got data for delta
    /// </summary>
    public string DeltaFromNode { get; set; }
    /// <summary>
    /// NodeId:Signature
    /// signatures of DeltaFromNode
    /// </summary>
    public List<ChainSignature> SourceSignatureChain { get; set; } = new();
    public string DeltaIdentifier { get; set; }

    /// <summary>
    /// NodeId:Signature
    /// signatures of the sending node 
    /// recent value must contain signature of DeltaFromNode
    /// </summary>
    public List<ChainSignature> SignatureChain { get; set; } = new();
    /// <summary>
    /// verification key of DeltaFromNode
    /// </summary>
    public byte[] DeltaVerifyKey { get; set; }
    /// <summary>
    /// nodes contained in delta
    /// </summary>
    public HashSet<string> Nodes = new();
}
public class ConsensusDeltaRequest
{
    //new synchronized items
    public string DeltaIdentifier { get; set; }
}

public class ConsensusDeltaOnDisk
{
    // Cryptographic assurance context tracking back to origin anchors
    public string DeltaFromNode { get; set; }
    public string DeltaIdentifier { get; set; }
    public List<string> NodeIds { get; set; } = new();
}
public class ConsensusDeltaDocument
{
    //new synchronized items
    public List<PeerNodeHistoric> DeltaNodes { get; set; } = new();
}
/// <summary>
/// Pair-wise node metadata wrapping both PeerNode parameters and subjective rating parameters 
/// for unified serialization within daily consensus partitions.
/// </summary>
public class WeightedConsensusNode
{
    public PeerNodeHistoric NodeData { get; set; } = new();

    public Dictionary<string, byte[]> NodeUpdateSignature { get; set; } = new();

    // Subjective Logic Opinion parameters approximating trust weights
    public double Belief { get; set; } = 0.5;
    public double Disbelief { get; set; } = 0.0;
    public double Uncertainty { get; set; } = 0.5;
}


//public class ConsensusRequest
//{
//    public string NodeIdFrom { get; set; }
//    public DateTime TimeStamp { get; set; }
//    public string Nonce { get; set; }
//    public bool NonTransitive { get; set; } //is this origin or transitive request
//    public byte[] Signature { get; set; }
//    public double? StartingRating { get; set; }

//    public byte[] GetHash()
//    {
//        return SHA256.HashData(Convert.FromBase64String(P2PCrypto.CrCompliantStr($"{NodeIdFrom}|{Nonce}|{TimeStamp}|{NonTransitive}")));
//    }

//}


public class ConsensusSignatureChain
{
    //new node may not know the key for verification
    public byte[] LastVerifyKey { get; set; }
    //however other keys should be known inherently
    public List<ChainSignature> ChainSignatures { get; set; } = new();
}