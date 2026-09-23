
using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using P2PModule.Configuration;
using P2PModule.Infrastructure;
using P2PModule.Interfaces;
using P2PModule.Models;
using P2PModule.Models.Network;
using P2PModule.Services.Persistence;
using P2PModule.Services.Rating;

using SIPSorcery.Net;


/// <summary>
/// State tracking class containing the mutual handshake transaction parameters.
/// </summary>
/// 
public class HandshakeSession
{
    public enum HandshakeType
    {
        Outgoing,
        Inbound
    } 

    public Guid HandshakeId { get; set; }
    public string PeerNodeId { get; set; } = string.Empty;
    public HandshakeType HandshakeDirection { get; set; }
    public HandshakeChallengeRequest Request { get; set; } = null!;
    //expectation for outgoing request, results for incoming
    public Dictionary<string, byte[]> ResponseExpectation { get; set; } = new();
    public HandshakeChallengeResponse Response { get; set; } = null!;
    //expectation for outgoing request, results for incoming
    public Dictionary<string, byte[]> ConfirmationExpectation { get; set; } = new();
    public HandshakeConfirmation Confirmation { get; set; } = null!;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
    public bool Mismatch { get; set; }
    public bool Handshaked { get; set; }

    public int PassedChallenges { get; set; }
    public int FailedChallenges { get; set; }
    public int UnknownChallenges { get; set; }
    public double Confidence { get; set; }
}


public class DoubleBlindChallengeService : IChallengeService, IDisposable
{
    private readonly ILogger<DoubleBlindChallengeService> _logger;
    private readonly IOptionsMonitor<P2PSettings> _settings;
    private readonly IOptionsMonitor<RatingSettings> _settingsRating;
    private readonly IOptionsMonitor<StorageSettings> _storageSettings;
    private readonly IPeerTracker _peerTracker;
    private readonly INodeIdentityService _identity;
    private readonly IRatingService _ratingService;
    private readonly IConsensusStore _archiveStore;
    //private readonly IServiceScopeFactory _scopeFactory;

    private readonly Timer _cleanupTimer;
    private readonly TimeSpan _challengeLifetime = TimeSpan.FromSeconds(30);//unhandshaked expiry
    private HashSet<string> _todayRequestedDays = new();

    //two ways of accessing sessions
    private readonly ConcurrentDictionary<Guid, HandshakeSession> _handshakeSessions = new ConcurrentDictionary<Guid, HandshakeSession>();
    private readonly ConcurrentDictionary<string, HandshakeSession> _nodeHandshakes = new ConcurrentDictionary<string, HandshakeSession>();

    public event Action<HandshakeSession>? OnHandshakeAuditFailed;
    private void InvokeOnHandshakeAuditFailed(HandshakeSession session)
    {
        var handlers = OnHandshakeAuditFailed?.GetInvocationList();

        if (handlers != null)
        {
            foreach (Action<HandshakeSession> handler in handlers)
            {
                _ = Task.Run(() =>
                {
                    try
                    {
                        handler(session);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Handshake audit handler failed.");
                    }
                });
            }
        }
    }
    public string HandshakeStartCommand => "HandshakeRequest";
    public bool IsHandshakeCommand(string commandType) => HandshakeCommands.Contains(commandType);
    private string[] HandshakeCommands => new string[] { "HandshakeRequest", "HandshakeResponse", "HandshakeConfirmation" };

    public DoubleBlindChallengeService(
        ILogger<DoubleBlindChallengeService> logger,
        IOptionsMonitor<P2PSettings> settings,
        IOptionsMonitor<RatingSettings> settingsRating,
        IOptionsMonitor<StorageSettings> storageSettings,
        IPeerTracker peerTracker,
        INodeIdentityService identity,
        IRatingService ratingService,
        IConsensusStore archiveStore//,
        //IServiceScopeFactory scopeFactory
        )
    {
        _peerTracker = peerTracker;
        _archiveStore = archiveStore;
        //_cryptoEngine = cryptoEngine;
        _ratingService = ratingService;
        _identity = identity;
        _settings = settings;
        _settingsRating = settingsRating;
        _storageSettings = storageSettings;
        _logger = logger;

        var handshakeTimeout = _settings.CurrentValue.DefaultHandshakeTimeoutSeconds;

        _cleanupTimer = new Timer(CleanStaleTokens, null,
            TimeSpan.FromSeconds(handshakeTimeout * 2),
            TimeSpan.FromSeconds(handshakeTimeout * 2));

        _challengeLifetime = TimeSpan.FromSeconds((int)(handshakeTimeout * 1.5));
        //_scopeFactory = scopeFactory;
    }

    public void RegisterHandshakedNode(string nodeId)
    {
        if(_nodeHandshakes.TryGetValue(nodeId, out var session))
        {
            PeerImmutableData identity;
            PeerNodeRuntime? peerNode;
            List<SignatureEnvelope> signatures;

            if (session.HandshakeDirection == HandshakeSession.HandshakeType.Inbound)
            {
                identity = session.Request.Identity;
                peerNode = _peerTracker.GetPeer(identity.NodeId);
                signatures = session.Confirmation.RequestedSignatures.Values.ToList();
            }
            else
            {
                identity = session.Response.Identity;
                peerNode = _peerTracker.GetPeer(identity.NodeId);
                signatures = session.Response.RequestedSignatures.Values.ToList();
            }

            if (peerNode != null)
            {
                peerNode.Identity = identity;
                peerNode.LastSeen = DateTime.UtcNow;
                peerNode.Signatures = signatures;
                //peerNode.Refresh.Clear();
            }
            else
            {
                peerNode = new PeerNodeRuntime
                {
                    Identity = identity,
                    LastSeen = DateTime.UtcNow,
                    Signatures = signatures,
                };
            }

            _peerTracker.UpdatePeerAsync(peerNode);
        }
    }

    #region Handshake stages

    private (List<ChallengeEnvelope>, Dictionary<string, byte[]>) GenerateChallengeEnvelopes(
        string proverNodeId, //node we need to make explicit challenge for
        Random rng)
    {
        var envelopes = new List<ChallengeEnvelope>();
        Dictionary<string, byte[]> expectedResults = new();

        //get days that that node was ON
        var inDays = _peerTracker.GetRecordedEpochsForNode(proverNodeId)
            .OrderBy(_ => rng.Next()).Take(16).ToList();
        inDays.Remove(DateTime.UtcNow.ToString("yyyy-MM-dd"));

        int sinceresNeeded = rng.Next(2, 6);
        int fakesNeeded = rng.Next(2, 6);

        for (int i = 0; i < sinceresNeeded && i < inDays.Count; i++)
        {
            string chosenDay = inDays[i];
            var peer = _archiveStore.GetHistoricalPeer(chosenDay, proverNodeId);

            if (peer != null)
            {
                byte[] filledNonce = new byte[32];
                RandomNumberGenerator.Fill(filledNonce);

                var env = new ChallengeEnvelope()
                {
                    DayKey = chosenDay,
                    EncryptedData = P2PCrypto.Encrypt(filledNonce, peer.Identity.PublicEncryptionKey)
                };

                // Sincere record: result is expected to match the stuff
                expectedResults[chosenDay] = filledNonce;
                envelopes.Add(env);
            }
        }

        for (int i = sinceresNeeded; i < (sinceresNeeded + fakesNeeded) && i < inDays.Count; i++)
        {
            string chosenDay = inDays[i];
            var peer = _archiveStore.GetHistoricalPeer(chosenDay, proverNodeId);

            if (peer != null)
            {
                byte[] filledNonce = new byte[32];
                RandomNumberGenerator.Fill(filledNonce);

                var env = new ChallengeEnvelope()
                {
                    DayKey = chosenDay,
                    EncryptedData = P2PCrypto.Encrypt(filledNonce, peer.Identity.PublicEncryptionKey)
                };

                //we do not expect anything
                //expectedResults[chosenDay] = filledNonce;
                envelopes.Add(env);
            }
        }

        return (envelopes, expectedResults);
    }

    /// <summary>
    /// Processes and decrypts incoming envelopes sequentially using historical consensus keys.
    /// Flags protocol contradictions out-of-bounds to prevent timing and error profiling attacks.
    /// </summary>
    public Dictionary<string, byte[]> ProcessEnvelopes(List<ChallengeEnvelope> envelopes)
    {
        var decryptedMap = new Dictionary<string, byte[]>();

        foreach (var env in envelopes)
        {
            var privateKey = _identity.GetPrivateKey(env.DayKey);

            if (privateKey != null)
            {
                decryptedMap[env.DayKey] = P2PCrypto.Decrypt(env.EncryptedData, privateKey);
            }
            else
            {
                //decrypt anyway
                decryptedMap[env.DayKey] = P2PCrypto.Decrypt(env.EncryptedData, _identity.PrivateKey);
            }
        }

        return decryptedMap;
    }

    /// <summary>
    /// Generates and signs a SignatureEnvelope for a given day key by fetching its matching private certificate.
    /// </summary>
    /// <param name="day">The target epoch day identifier (e.g., "YYYY-MM-DD").</param>
    /// <returns>A signed SignatureEnvelope ready for transmission, or null if key material is missing.</returns>
    public SignatureEnvelope? GenerateSignature(string day, byte[] incomingHash)
    {
        if (string.IsNullOrEmpty(day)) return null;

        byte[] nonce = new byte[32],
            nonceSigned = new byte[32 + incomingHash.Length];
        RandomNumberGenerator.Fill(nonce);

        Buffer.BlockCopy(nonce, 0, nonceSigned, 0, nonce.Length);
        Buffer.BlockCopy(incomingHash, 0, nonceSigned, 32, incomingHash.Length);

        // Fetch private signature certificate for the historical day
        byte[]? privateKey = _identity.GetPrivateCertificate(day);

        // Fallback to active static private signature key if requested day is current or missing
        if (privateKey == null || privateKey.Length == 0)
        {
            privateKey = _identity.PrivateSignatureKey;
        }

        if (privateKey == null || privateKey.Length == 0)
        {
            _logger.LogWarning("[Blind Challenge] Unable to generate SignatureEnvelope for day {Day}: Private key/certificate is missing.", day);
            return null;
        }

        try
        {
            byte[] signature = P2PCrypto.SignData(nonceSigned, privateKey);
            return new SignatureEnvelope
            {
                DayKey = day,
                Nonce = nonce,
                Signature = signature
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Blind Challenge] Asymmetric cryptographic signing failed for day {Day}", day);
            return null;
        }
    }

    /// <summary>
    /// Compiles signed signature envelopes for all requested days.
    /// </summary>
    public Dictionary<string, SignatureEnvelope> GenerateSignatures(byte[] hash)
    {
        return GenerateSignatureEnvelopes(_todayRequestedDays, hash);
    }


    /// <summary>
    /// Compiles signed signature envelopes for all requested days.
    /// </summary>
    private Dictionary<string, SignatureEnvelope> GenerateSignatureEnvelopes(IEnumerable<string> days, byte[] hash)
    {
        var result = new Dictionary<string, SignatureEnvelope>();
        if (days == null) return result;

        foreach (var day in days)
        {
            var envelope = GenerateSignature(day, hash);
            if (envelope != null)
            {
                result[day] = envelope;
            }
        }

        return result;
    }


    public HandshakeChallengeRequest CreateHandshakeRequest(string proverNodeId)
    {
        CleanExpiredChallenges();

        string todayKey = DateTime.UtcNow.ToString("yyyy-MM-dd");

        _logger.LogInformation("[Challenge Engine] Compiling randomized challenge envelopes for peer: {PeerId}", proverNodeId);

        var request = new HandshakeChallengeRequest
        {
            Identity = _identity.GetImmutableData(),
            SenderUtc = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };

        // Flat tracking dictionary map: Key = EncryptedData (practically collision-free), Value = (wasIn, DecryptedText)
        var challengeSession = new HandshakeSession
        {
            HandshakeId = Guid.NewGuid(),
            PeerNodeId = proverNodeId,
            HandshakeDirection = HandshakeSession.HandshakeType.Outgoing,
            Request = request
        };
        var rng = new Random();

        int requests = rng.Next(4, 8);
        request.RequestedDays = _peerTracker.GetRecordedEpochsForNode(proverNodeId)
            .OrderBy(_ => rng.Next()).Take(requests).ToList();
        request.RequestedDays.Remove(todayKey);

        var (challengeEnvelopes, expectations) = GenerateChallengeEnvelopes(proverNodeId, rng);
        // Shuffle envelopes to obscure order
        request.Envelopes = challengeEnvelopes.OrderBy(_ => rng.Next()).ToList();
        challengeSession.ResponseExpectation = expectations;

        _handshakeSessions[request.HandShakeId] = challengeSession;
        _nodeHandshakes[proverNodeId] = challengeSession;

        return request;
    }


    /// <summary>
    /// STEP 2: Process the incoming HandshakeChallengeRequest.
    /// Creates an inbound session tracking state, decrypts all envelopes blindly, 
    /// and implements the silent-disconnect penalty logic if validation contradictions are triggered.
    /// </summary>
    public HandshakeChallengeResponse ProcessChallengeRequestAsync(HandshakeChallengeRequest request)
    {
        _logger.LogInformation("[Challenge Engine] Step 2: Processing challenge handshake from: {NodeId}", request.Identity.NodeId);

        var localNodeId = _identity.GetLocalNodeId();
        var signalling = _identity.GetEndpoints();
        var response = new HandshakeChallengeResponse
        {
            HandShakeId = request.HandShakeId,
            Identity = _identity.GetImmutableData(),
            SenderUtc = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };

        //
        //{
        //    NodeId = _identity.NodeId,
        //    PublicVerifyKey = _identity.PublicVerifyKey,
        //    PublicEncryptionKey = _identity.PublicKey,
        //    SignalingEndpoints = _identity.GetEndpoints(),
        //    Roles = _identity.GetCapacities(),
        //    SignatureTime = DateTime.UtcNow
        //},
        //response.Identity.Signature = P2PCrypto.SignData(response.Identity.GetNodeDataHash(), _identity.PrivateSignatureKey);

        // Initialize inbound session tracking
        var inboundSession = new HandshakeSession
        {
            HandshakeId = request.HandShakeId,
            PeerNodeId = request.Identity.NodeId,
            HandshakeDirection = HandshakeSession.HandshakeType.Inbound,
        };
        inboundSession.Response = response;
        inboundSession.Request = request;

        // Blindly process all incoming envelopes
        // i prove that i know you
        response.EvaluationResults = ProcessEnvelopes(request.Envelopes);
        // response results expected
        inboundSession.ResponseExpectation = response.EvaluationResults; 

        //we prove who we are, we sign our stuff
        response.RequestedSignatures = GenerateSignatureEnvelopes(request.RequestedDays, response.Identity.GetNodeDataHash());

        var rng = new Random();

        int requests = rng.Next(4, 8);
        response.RequestedDays = _peerTracker.GetRecordedEpochsForNode(request.Identity.NodeId).OrderBy(_ => rng.Next()).Take(requests).ToList();
        response.RequestedDays.Remove(DateTime.UtcNow.ToString("yyyy-MM-dd"));

        var (challengeEnvelopes, expectations) = GenerateChallengeEnvelopes(request.Identity.NodeId, rng);
        // Shuffle envelopes to obscure order
        response.Envelopes = challengeEnvelopes.OrderBy(_ => rng.Next()).ToList();
        inboundSession.ConfirmationExpectation = expectations;

        // Cache the session state
        _handshakeSessions[request.HandShakeId] = inboundSession;
        _nodeHandshakes[request.Identity.NodeId] = inboundSession;

        return response;
    }

    /// <summary>
    /// STEP 3: Process challenge response.
    /// Evaluates responder's decrypted results, verifies timeline parameters, 
    /// Evaluates responders signatures
    /// decrypts responder's challenges, and generates a HandshakeConfirmation.
    /// </summary>
    public async Task<(HandshakeConfirmation?, bool)> ProcessChallengeResponse(HandshakeChallengeResponse response)
    {
        _logger.LogInformation("[Challenge Engine] Step 3: Processing challenge response from: {NodeId}", response.Identity.NodeId);

        var localNodeId = _identity.GetLocalNodeId();

        // Retrieve our original outgoing session
        if (!_handshakeSessions.TryGetValue(response.HandShakeId, out var session))
        {
            _logger.LogWarning("[Challenge Engine] Step 3: Handshake session {Id} not found or expired.", response.HandShakeId);
            return (null, false);
        }

        var confirmation = new HandshakeConfirmation
        {
            NodeId = localNodeId,
            HandShakeId = response.HandShakeId,
            VerifierReceivedUtc = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
        session.Response = response;
        session.Confirmation = confirmation;

        // 1 check decoding
        //int failedEnvelopes = 0;
        foreach (var expectation in session.ResponseExpectation)
        {
            string encryptedDataKey = expectation.Key;
            var expectedRecord = expectation.Value; // string expectedPlaintextB64

            if (response.EvaluationResults.TryGetValue(encryptedDataKey, out var actualDecryptedB64))
            {
                if (!actualDecryptedB64.SequenceEqual(expectedRecord))
                {
                    //++failedEnvelopes;
                    ++session.FailedChallenges;
                }
                else
                {
                    ++session.PassedChallenges;
                }
            }
            else
            {
                //all envelopes should be processed
                _logger.LogWarning("[Challenge Engine] Responder omitted expected envelope result. Data: {Key}", encryptedDataKey);
                ++session.UnknownChallenges;
            }
        }

        // 2 verify signatures
        byte[] nodeIdentityHash = session.Response.Identity.GetNodeDataHash();
        byte[] nonceTarget = new byte[32 + nodeIdentityHash.Length];
        Buffer.BlockCopy(nodeIdentityHash, 0, nonceTarget, 32, nodeIdentityHash.Length);

        foreach (var signatureDay in session.Request.RequestedDays)
        {
            if (response.RequestedSignatures.TryGetValue(signatureDay, out var signatureEnvelope))
            {
                var peerNodeKey = _archiveStore.GetHistoricalPeer(signatureDay, session.PeerNodeId);
                if(peerNodeKey != null)
                {
                    Buffer.BlockCopy(signatureEnvelope.Nonce, 0, nonceTarget, 0, 32);
                    if (!P2PCrypto.TestSignature(
                        nonceTarget, 
                        peerNodeKey.Identity.PublicVerifyKey, 
                        signatureEnvelope.Signature))
                    {
                        //++failedEnvelopes;
                        ++session.FailedChallenges;
                    }
                    else
                    {
                        ++session.PassedChallenges;
                    }
                }
                else
                {
                    //we got some trash in our database, omit this
                    _logger.LogWarning("[Handshake Security] Responder {nodeId} is not found on {day}.", session.PeerNodeId, signatureDay);
                }
            }
            else
            {
                ++session.UnknownChallenges;
                _logger.LogWarning("[Handshake Security] Responder {nodeId} omited signature day {day}.", session.PeerNodeId, signatureDay);
            }
        }

        session.Confidence =
            (double)(session.PassedChallenges - session.FailedChallenges) /
            (session.PassedChallenges +
             session.FailedChallenges +
             session.UnknownChallenges);

        session.Mismatch = true;
        if (session.Confidence > 0)
        {
            session.Mismatch = false;
        }

        // 3. Mark completion Date if verification audits succeeded
        if (!session.Mismatch)
        {
            session.Handshaked = true;
            session.CompletedAt = DateTime.UtcNow;

            _logger.LogInformation("[Challenge Engine] Outgoing handshake successfully verified. CompletionDate set.");

            _ = _ratingService.ApplyLocalReputationUpdateAsync(session.PeerNodeId,
                baseAmount: _settingsRating.CurrentValue.HandshakeRepDelta * session.Confidence);
        }
        else
        {
            session.Handshaked = false;
            _logger.LogWarning("[Challenge Engine] Audit checks failed. Proceeding silently to prevent error leakage profile.");
            _ = _ratingService.ApplyLocalReputationUpdateAsync(session.PeerNodeId,
                baseAmount: -_settingsRating.CurrentValue.HandshakeRepDelta);
        }
        // 3 Blindly process response provided envelopes (Solve challenges)
        confirmation.EvaluationResults = ProcessEnvelopes(response.Envelopes);
        session.ConfirmationExpectation = confirmation.EvaluationResults;

        // 4 generate responce envelopes 
        confirmation.RequestedSignatures = GenerateSignatureEnvelopes(response.RequestedDays, session.Request.Identity.GetNodeDataHash());
        
        if(!session.Mismatch)
            _todayRequestedDays.UnionWith(session.Response.RequestedDays);

        // send away
        return (confirmation, session.Handshaked);
    }


    /// <summary>
    /// STEP 4: Responder verifies HandshakeConfirmation.
    /// Overload targeting raw HandshakeConfirmation payload.
    /// </summary>
    public async Task<bool> ProcessChallengeConfirmation(HandshakeConfirmation confirmation)
    {
        _logger.LogInformation("[Challenge Engine] Step 4: Processing handshake confirmation from: {NodeId}", confirmation.NodeId);

        if (!_handshakeSessions.TryGetValue(confirmation.HandShakeId, out var session))
        {
            _logger.LogWarning("[Challenge Engine] Step 4: Handshake session {Id} not found.", confirmation.HandShakeId);
            return false;
        }
        session.Confirmation = confirmation;

        //int failedEnvelopes = 0;
        foreach (var expectation in session.ConfirmationExpectation)
        {
            string encryptedDataKey = expectation.Key;
            var expectedRecord = expectation.Value; // string expectedPlaintextB64

            if (confirmation.EvaluationResults.TryGetValue(encryptedDataKey, out var actualDecryptedB64))
            {
                if (!actualDecryptedB64.SequenceEqual(expectedRecord))
                {
                    //++failedEnvelopes;
                    ++session.FailedChallenges;
                }
                else
                {
                    ++session.PassedChallenges;
                }
            }
            else
            {
                //all envelopes should be processed
                _logger.LogWarning("[Challenge Engine] Responder omitted expected envelope result. Data: {Key}", encryptedDataKey);
                //++failedEnvelopes;
                ++session.UnknownChallenges;
            }
        }

        // 2 verify signatures
        byte[] nodeIdentityHash = session.Request.Identity.GetNodeDataHash();
        byte[] nonceTarget = new byte[32 + nodeIdentityHash.Length];
        Buffer.BlockCopy(nodeIdentityHash, 0, nonceTarget, 32, nodeIdentityHash.Length);

        foreach (var signatureDay in session.Response.RequestedDays)
        {
            if (confirmation.RequestedSignatures.TryGetValue(signatureDay, out var signatureEnvelope))
            {
                var peerNodeKey = _archiveStore.GetHistoricalPeer(signatureDay, session.PeerNodeId);
                if (peerNodeKey != null)
                {
                    Buffer.BlockCopy(signatureEnvelope.Nonce, 0, nonceTarget, 0, 32);
                    if (!P2PCrypto.TestSignature(
                        nonceTarget,
                        peerNodeKey.Identity.PublicVerifyKey,
                        signatureEnvelope.Signature))
                    {
                        //++failedEnvelopes;
                        ++session.FailedChallenges;
                    }
                    else
                    {
                        ++session.PassedChallenges;
                    }
                }
                else
                {
                    //we got some trash in our database, omit this
                    _logger.LogWarning("[Handshake Security] Responder {nodeId} is not found on {day}.", session.PeerNodeId, signatureDay);
                }
            }
            else
            {
                //++failedEnvelopes;
                _logger.LogWarning("[Handshake Security] Responder {nodeId} omited signature day {day}.", session.PeerNodeId, signatureDay);
                ++session.UnknownChallenges;
            }
        }

        session.Confidence =
            (double)(session.PassedChallenges - session.FailedChallenges) /
            (session.PassedChallenges +
             session.FailedChallenges +
             session.UnknownChallenges);

        session.Mismatch = true;
        if (session.Confidence > 0)
        {
            session.Mismatch = false;
        }


        if (!session.Mismatch)
        {
            session.Handshaked = true;
            session.CompletedAt = DateTime.UtcNow;
            _logger.LogInformation("[Challenge Engine] Step 4: Handshake successfully handshaked on both sides.");
            _ = _ratingService.ApplyLocalReputationUpdateAsync(confirmation.NodeId, baseAmount: _settingsRating.CurrentValue.HandshakeRepDelta * session.Confidence);

            _todayRequestedDays.UnionWith(session.Request.RequestedDays);

            return true;
        }

        session.Handshaked = false;
        _logger.LogWarning("[Challenge Engine] Handshake confirmation failed check. Proceeding silently to prevent error leakage profile.");
        _ = _ratingService.ApplyLocalReputationUpdateAsync(confirmation.NodeId, baseAmount: -_settingsRating.CurrentValue.HandshakeRepDelta);
        return false;
    }


    #endregion
    private void SendMessageRaw(string payload, RTCDataChannel channel)
    {
        if (channel.readyState == RTCDataChannelState.open)
        {
            channel.send(payload);
        }
    }

    /// <summary>
    /// Executes the simplified 3-step WebRTC signaling validation logic. 
    /// </summary>
    public async Task<bool> HandleHandshakeMessage(string nodeId, RTCDataChannel channel, P2PPacketEnvelope envelope)
    {
        try
        {
            if (envelope == null) return false;

            if (envelope.CommandType == "HandshakeRequest")
            {
                var requestPayload = JsonSerializer.Deserialize<HandshakeChallengeRequest>(envelope.SerializedPayload);
                if (requestPayload != null)
                {
                    // STEP 2: Process request (Bypasses signature verification to avoid blockades)
                    var responsePayload = ProcessChallengeRequestAsync(requestPayload);

                    var responseEnvelope = new P2PPacketEnvelope
                    {
                        SenderNodeId = _identity.GetLocalNodeId(),
                        TargetNodeId = nodeId,
                        CommandType = "HandshakeResponse",
                        SerializedPayload = JsonSerializer.Serialize(responsePayload),
                        Ttl = 1
                    };
                    SendMessageRaw(JsonSerializer.Serialize(responseEnvelope), channel);
                }
            }
            else
            if (envelope.CommandType == "HandshakeResponse")
            {
                var responsePayload = JsonSerializer.Deserialize<HandshakeChallengeResponse>(envelope.SerializedPayload);
                if (responsePayload != null)
                {
                    // STEP 2: Process request (Bypasses signature verification to avoid blockades)
                    var (confirmationPayload, handshaked) = await ProcessChallengeResponse(responsePayload);
                    
                    var responseEnvelope = new P2PPacketEnvelope
                    {
                        SenderNodeId = _identity.GetLocalNodeId(),
                        TargetNodeId = nodeId,
                        CommandType = "HandshakeConfirmation",
                        SerializedPayload = JsonSerializer.Serialize(confirmationPayload),
                        Ttl = 1
                    };
                    SendMessageRaw(JsonSerializer.Serialize(responseEnvelope), channel);
                    if (handshaked)
                    {
                        RegisterHandshakedNode(nodeId);
                    }
                }
            }
            else if (envelope.CommandType == "HandshakeConfirmation")
            {
                var confirmationPayload = JsonSerializer.Deserialize<HandshakeConfirmation>(envelope.SerializedPayload);
                if (confirmationPayload != null)
                {
                    _logger.LogInformation("[Handshake] WebRTC final step confirmation established for inbound peer {NodeId}.", nodeId);

                    var handshaked = await ProcessChallengeConfirmation(confirmationPayload);
                    if (handshaked)
                    {
                        RegisterHandshakedNode(nodeId);
                    }
                    return IsRecentlyHandshaked(nodeId);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Handshake Execution] Live handshake flow dropped on node {NodeId}.", nodeId);
        }
        return false;
    }


    /// <summary>
    /// Executes the simplified 3-step HTTP signaling validation logic. 
    /// </summary>
    public async Task<string> HandleHandshakeMessage(HttpRequest request)
    {
        var envelope = await request.ReadFromJsonAsync<P2PPacketEnvelope>();
        if (envelope == null) return string.Empty;

        try
        {

            if (envelope.CommandType == "HandshakeRequest")
            {
                var requestPayload = JsonSerializer.Deserialize<HandshakeChallengeRequest>(envelope.SerializedPayload);
                if (requestPayload != null)
                {
                    // STEP 2: Process request (Bypasses signature verification to avoid blockades)
                    var responsePayload = ProcessChallengeRequestAsync(requestPayload);

                    var responseEnvelope = new P2PPacketEnvelope
                    {
                        SenderNodeId = _identity.NodeId,
                        TargetNodeId = requestPayload.Identity.NodeId,
                        CommandType = "HandshakeResponse",
                        SerializedPayload = JsonSerializer.Serialize(responsePayload),
                        Ttl = 1
                    };

                    return JsonSerializer.Serialize(responseEnvelope);
                }
            }
            else if (envelope.CommandType == "HandshakeConfirmation")
            {
                var confirmationPayload = JsonSerializer.Deserialize<HandshakeConfirmation>(envelope.SerializedPayload);
                if (confirmationPayload != null)
                {
                    _logger.LogInformation("[Handshake] WebRTC final step confirmation established for inbound peer {NodeId}.", confirmationPayload.NodeId);

                    if(await ProcessChallengeConfirmation(confirmationPayload) && IsRecentlyHandshaked(confirmationPayload.NodeId))
                    {
                        RegisterHandshakedNode(confirmationPayload.NodeId);

                        return true.ToString();
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Handshake Execution] Live handshake flow dropped on node {NodeId}.", envelope.SenderNodeId);
        }

        return string.Empty;
    }



    public void Dispose()
    {
        _cleanupTimer?.Dispose();
    }

    private void CleanExpiredChallenges()
    {
        var now = DateTime.UtcNow;

        var expiredKeys = _handshakeSessions.Where(kv => !kv.Value.Handshaked && (now - kv.Value.CreatedAt) > _challengeLifetime).Select(kv => kv.Key).ToList();
        foreach (var key in expiredKeys) _handshakeSessions.TryRemove(key, out _);
    }

    private void CleanStaleTokens(object? state)
    {
        var cleanNow = DateTime.UtcNow.AddSeconds(-_settings.CurrentValue.DefaultHandshakeTimeoutSeconds * 3);

        var sessions = _handshakeSessions.Values.Where(s => s.CompletedAt < cleanNow);
        foreach (var session in sessions)
        {
            _handshakeSessions.Remove(session.Request.HandShakeId, out _);
            _nodeHandshakes.Remove(session.PeerNodeId, out _);

            if (!session.Handshaked)
            {
                InvokeOnHandshakeAuditFailed(session);
            }
        }

        var maxAllowed = _storageSettings.CurrentValue.RetainedHistoryEpochsCount;
        if (_todayRequestedDays.Count > maxAllowed)
        {
            var oldestDays = _todayRequestedDays
                .OrderBy(d => d, StringComparer.Ordinal)
                .Take(_todayRequestedDays.Count - maxAllowed)
                .ToHashSet();

            _todayRequestedDays.ExceptWith(oldestDays);
        }
    }

    public bool IsRecentlyHandshaked(string nodeId)
    {
        if(_nodeHandshakes.TryGetValue(nodeId, out var handshake)) {
            return handshake.Handshaked;
        }
        return false;
    }

    public async Task<Guid?> DoHttpConnectionChallenge(HttpClient httpClient, string nodeId, string targetEndpoint)
    {
        // STEP 1: Create request
        //"HandshakeRequest"
        HandshakeChallengeRequest handshakeReq = CreateHandshakeRequest(nodeId);
        P2PPacketEnvelope envelope = new P2PPacketEnvelope
        {
            SenderNodeId = _identity.NodeId,
            TargetNodeId = nodeId,
            CommandType = "HandshakeRequest",
            SerializedPayload = JsonSerializer.Serialize(handshakeReq),
            Ttl = 1
        };

        using var msg = new HttpRequestMessage(HttpMethod.Post, $"{targetEndpoint}/p2p/handshake/challenge") { Content = JsonContent.Create(envelope) };
        using var response = await httpClient.SendAsync(msg);
        if (response.IsSuccessStatusCode)
        {
            envelope = (await response.Content.ReadFromJsonAsync<P2PPacketEnvelope>())!;

            if (envelope == null) IsRecentlyHandshaked(nodeId);
            else
            if (envelope.CommandType == "HandshakeResponse")
            {
                var handshakeResponse = JsonSerializer.Deserialize<HandshakeChallengeResponse>(envelope.SerializedPayload);
                if (handshakeResponse != null)
                {
                    // STEP 2: Audit response (Checks decryption correctness, skips Signature 2 validation)
                    var (confirmation, handshaked) = await ProcessChallengeResponse(handshakeResponse);

                    envelope = new P2PPacketEnvelope
                    {
                        SenderNodeId = _identity.NodeId,
                        TargetNodeId = nodeId,
                        CommandType = "HandshakeConfirmation",
                        SerializedPayload = JsonSerializer.Serialize(confirmation),
                        Ttl = 1
                    };

                    // STEP 3: Initiator compiles HandshakeConfirmation
                    using var auditMsg = new HttpRequestMessage(HttpMethod.Post, $"{targetEndpoint}/p2p/handshake/challenge") { Content = JsonContent.Create(envelope) };
                    using var auditResult = await httpClient.SendAsync(auditMsg);

                    if (handshaked)
                    {
                        RegisterHandshakedNode(nodeId);
                    }
                }
            }
        }

        if (IsRecentlyHandshaked(nodeId) && _nodeHandshakes.TryGetValue(nodeId, out var session))
        {
            return session.HandshakeId;
        }

        return null;
    }
}