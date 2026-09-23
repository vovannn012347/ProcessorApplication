using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Http;

using P2PModule.Models;
using P2PModule.Models.Gossips;
using P2PModule.Models.Network;
using P2PModule.Services.Connection;

using SIPSorcery.Net;

using static P2PModule.Services.Connection.PeerConnectionManager;

namespace P2PModule.Interfaces;


public interface IConsensusStore
{
    /// <summary>
    /// returns available historic days from recent to latest
    /// </summary>
    /// <returns></returns>
    List<string> GetAvailableHistoricalDays();
    List<string> GetHistoricalPeers(string dayKey);
    PeerNodeHistoric? GetHistoricalPeer(string dayKey, string nodeId);
    void UpdateHistoricalPeer(string dayKey, string nodeId, PeerNodeHistoric peer);
    Task PruneHistoryAsync(); //prune history according to settings

    // Watchdog and Persistence Compatibility
    bool IsOutdated();
}

public interface INodeIdentityService
{
    public string NodeId { get; }
    void LoadOrCreateIdentity();
    string GetLocalNodeId();

    //i prove who am i
    byte[] PublicKey { get; } //today
    byte[] PrivateKey { get; } //today
    byte[]? GetPublicKey(string day = "");
    byte[]? GetPrivateKey(string day = "");

    //this proves i signed it
    byte[] PublicVerifyKey { get; } //today
    byte[] PrivateSignatureKey { get; } //today
    byte[]? GetPublicVerificator(string day = "");
    byte[]? GetPrivateCertificate(string day = "");
    //byte[] RefreshVerify { get; } //today ONLY
    //byte[] RefreshCert { get; } //today ONLY
    //byte[]? GetPublicRefresh(string day = "");
    //byte[]? GetPrivateRefresh(string day = "");
    List<string> GetEndpoints();
    List<string> GetCapablities();
    bool HasRole(string role);
    void ResetIdentity();
    void UpdateDailyKeys(bool forceUpdate = false);
    //trigger update stuffs
    void TriggerUpdate();

    /// <summary>
    /// Gets a list of last n available historical day keys sorted from most recent to least recent (descending order).
    /// </summary>
    List<string> GetRecentDayKeys(int n);
    PeerImmutableData GetImmutableData();

    /// <summary>
    /// returns old identity day key
    /// </summary>
    event Action<string>? OnIdentityUpdated;
}

public interface IConsensusCoordinatorService
{
    // File-Oriented Consolidation State Machine (Iteration Merge Operations)
    Task StartConsensusMergeAsync();
    HashSet<string>? StageIncomingNodesAsync(List<PeerNodeTransferred> doc, string fromNodeId);
    void FinalizeConsensusIterationMergeAsync();
    Task FinalizeConsensusMergeAsync(string dayKey = "", bool verify = true); //by default use the date component of UtcNow
    Task AbortConsensusMergeAsync();
    Task ResetIterationMergeAsync();

    //event Action NodeAdvertiseRequired;

    event Action ConsensusStarted;

    event Action ConsensusEnded;
}

public interface IMeshManagementService
{
    Task PerformInitialHandshakeAsync();
    Task MaintainOutboundConnectionsAsync();
    Task<bool> DiscoverNewConsensus();
    Task PingNodesAsync();
    Task<bool> SendHeartbeatAsync(string nodeId);
    Task<bool> CheckEndpointsALiveAsync(string nodeId);
}

public interface IP2PGossipService
{
    void Subscribe(string topic, Func<GossipMessage, string /* senderNodeId */, Task> handler);
    Task PublishAsync(string topic, string payload, int priority);
    Task PublishAsync(string topic, string payload, int priority, string signWithDay);
}

public interface IChallengeService
{
    HandshakeChallengeRequest CreateHandshakeRequest(string proverNodeId);
    bool IsHandshakeCommand(string commandType);
    string HandshakeStartCommand { get; }
    Task<bool> HandleHandshakeMessage(string nodeId, RTCDataChannel channel, P2PPacketEnvelope lookaheadHeader);
    Task<string> HandleHandshakeMessage(HttpRequest request);
    //void RegisterHandshakedNode(string nodeId);
    /// <summary>
    /// 
    /// </summary>
    /// <param name="http">configured client</param>
    /// <param name="nodeId">node identifier obtained through identity endpoint</param>
    /// <param name="targetEndpoint">endpoint?</param>
    /// <returns></returns>
    Task<Guid?> DoHttpConnectionChallenge(HttpClient http, string nodeId, string targetEndpoint);
    bool IsRecentlyHandshaked(string nodeId);

    /// <summary>
    /// generates signatures by internal challenge service rules
    /// </summary>
    /// <param name="hash">hash to sign</param>
    /// <returns></returns>
    Dictionary<string, SignatureEnvelope> GenerateSignatures(byte[] hash);


    event Action<HandshakeSession>? OnHandshakeAuditFailed;
}
