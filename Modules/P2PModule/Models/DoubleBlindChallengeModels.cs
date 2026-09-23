using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace P2PModule.Models;

public static class Zero
{
    public static byte[] Byte = Array.Empty<byte>();
}

public class AuditTimePeriod
{   
    public DateTime MemoryStartDate { get; set; }
    public DateTime MemoryEndDate { get; set; }
    public List<DateTime> ExplicitActiveDays { get; set; } = new();
    public bool ContainsDay(string dayKey)
    {
        if (DateTime.TryParse(dayKey, out var parsedDate))
        {
            return parsedDate >= MemoryStartDate && parsedDate <= MemoryEndDate;
        }
        return false;
    }

    public bool WasPreciselyActiveIn(string dayKey)
    {
        if (DateTime.TryParse(dayKey, out var parsedDate))
        {
            return ExplicitActiveDays.Contains(parsedDate);
        }
        return false;
    }
}

public class ChallengeEnvelope
{
    public string DayKey { get; set; } = string.Empty;
    public byte[] EncryptedData { get; set; } = Zero.Byte;
}

public class ChallengeEnvelopeResult
{
    public string DayKey { get; set; } = string.Empty;
    //expected nonce
    public string ResultData { get; set; } = string.Empty;
}

public class HandshakeChallengeRequest
{
    public Guid HandShakeId { get; set; } = Guid.NewGuid();
    //our handshake identity
    public PeerImmutableData Identity { get; set; } = new();

    //requested days with signatures we know about
    public List<string> RequestedDays { get; set; } = new();

    //signature for request with challenge envelopes
    //contains hash of above data and request signature using private key
    public List<ChallengeEnvelope> Envelopes { get; set; } = new();//one-side challenge

    // T1: The exact UTC time the initiator dispatched this challenge
    public long SenderUtc { get; set; }
}

public class HandshakeChallengeResponse
{
    public Guid HandShakeId { get; set; }
    public PeerImmutableData Identity { get; set; } = new();

    //our responder advertised retained history days to check against
    public List<string> RequestedDays { get; set; } = new();
    public Dictionary<string, SignatureEnvelope> RequestedSignatures { get; set; } = new();
    public List<ChallengeEnvelope> Envelopes { get; set; } = new(); //two-side challenge
    public Dictionary<string, byte[]> EvaluationResults { get; set; } = new();

    // T2: The exact UTC time the responder received the challenge
    // T3: The exact UTC time the responder dispatched this response
    public long SenderUtc { get; set; }

}

public class HandshakeConfirmation
{
    public Guid HandShakeId { get; set; }
    public string NodeId { get; set; } = string.Empty;
    //we have exchanged data already, no need to re-pass it
    //only second blind challenge results are here and their signature hash
    public Dictionary<string, byte[]> EvaluationResults { get; set; } = new();
    public Dictionary<string, SignatureEnvelope> RequestedSignatures { get; set; } = new();

    // T4: The exact UTC time the initiator received the response
    public long VerifierReceivedUtc { get; set; }
}

/// <summary>
/// Represents a single hop in the Gossip distribution chain.
/// Binds time and cryptographic signatures to prevent fake network spread generation.
/// </summary>
public class GossipHop
{
    public string NodeId { get; set; } = string.Empty;
    public long TimestampEpochMs { get; set; }
    public string HopSignature { get; set; } = string.Empty;
}