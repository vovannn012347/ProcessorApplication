using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace P2PModule.Configuration;

public class P2PSettings
{
    public string EnforcedNodeIdentifier { get; set; } = String.Empty;
    // Node Identity & Topology
    /// <summary>
    /// endpoints
    /// </summary>
    public List<string> Endpoints { get; set; } = new();
    /// <summary>
    /// "Tracker", "Promoted_Tracker", possibly other roles
    /// </summary>
    public List<string> NodeRoles { get; set; } = new();

    // Core P2P Configuration
    public string DataStoragePath { get; set; } = "./Module_Data/P2P";
    public int StartupDelaySeconds { get; set; } = 15;
    public int TargetOutboundConnections { get; set; } = 3;
    //for seeding up the initial conenctions
    public List<string> SeedEndpoints { get; set; } = new();
    //nat punch servers
    public List<string> StunServers { get; set; } = new();


    // Connection & Liveness
    //public int ActiveNodeSecondsTimeSpan { get; set; } = 604800; // 7 days, in seconds
    public double KeepAlivePeriodMs { get; set; } = 3000;
    public int ActiveNodeSecondsBackupPeriod { get; set; } = 300; // 5 mins
    public int TopologyMaintenannceIntervalMs { get; set; } = 30000;

    public int HttpTimeoutSeconds { get; set; } = 10; //default timeout
    public int DefaultHandshakeTimeoutSeconds { get; set; } = 10;
    public int MaxRelayInactivitySeconds { get; set; } = 45;
    public int DefaultRequestTimeoutSeconds { get; set; } = 10;
    public int TimeSyncIntervalSeconds { get; set; } = 120;
}

public class StorageSettings
{
    public int PersistTimePeriodSeconds { get; set; } = 300;

    //amount of nodes persisted to disk on flush, anty-dos sybil attack
    public int MaxNewNodesDrip { get; set; } = 10;
    //how many past history days are retained in absolure epochs
    public int RetainedHistoryEpochsCount { get; set; } = 7;
    //minimum epochs of per-node history required to retain
    //so if node was not recently active - atleast two last epochs for that node are retained
    //used for history retaining, days after those are prunned after user request
    //also used for handshakes
    public int RetainedNodeHistoryEpochs { get; set; } = 2;
}

public class GossipSettings
{
    public int MaxNewNodesDrip { get; set; } = 10; //10 new nodes in 5 minutes, more = pay in rating
    public int NodeDripPenalty { get; set; } = 2; //rating payment
    public int GossipNodesSeconds { get; set; } = 300;

    public int FanoutTargetCount { get; set; } = 3;
    //amount of gossips in batch to create on update
    public int GossipInBatches { get; set; } = 10;

    public int GossipStaleAgeMs { get; set; } = 60000;
    public int SeenReportCacheTtlMinutes { get; set; } = 30;
}

public class RatingSettings
{
    public double HandshakeRepDelta { get; set; } = 2;
    public double SenioritySmoothingCoefficient { get; set; } = 15;
    public double MinimumTrustThreshold { get; set; } = -10;
    public double PositiveTrustDecayRate { get; set; } = 0.05;
    public double NegativeTrustIndulgenceRate { get; set; } = 0.25;
    public double ConsensusSelfTrust { get; set; } = 0.6; // 0.0 to 1.0
    /// <summary>
    /// minimum threshold to start consensus forced out of time
    /// </summary>
    public double ConsensusOutOfTImeStartThreshold { get; set; } = 15;
    /// <summary>
    /// demote node rating amount on forced consensus
    /// </summary>
    public double ConsensusOutOfTImePrice { get; set; } = 15;
}