using System.Text.Json;

using Common.Attributes;
using Common.Interfaces;
using Common.Models;

using ProcessorApplication.Models.Nodes;

namespace ProcessorApplication.Models;

[SettingKey(P2PSettings.SECTION)]
public class P2PSettings : ISettings
{
    public const string SECTION = "P2P";

    public const string Port_NAME = "Port";
    public int Port { get; set; } = 5000;


    public const string UpdateSeconds_NAME = "UpdateSeconds";
    public double UpdateSeconds { get; set; } = 3;


    public const string P2PRole_NAME = "P2PRole";
    public NodeRole P2PRole { get; set; } = NodeRole.Peer;


    public const string KNearestNodes_NAME = "KNearestNodes";
    public int KNearestNodes { get; set; } = 3;


    public const string RelayTTL_NAME = "RelayTTL";
    public int RelayTTL { get; set; } = 4;


    public const string DefaultTrackers_NAME = "DefaultTrackers";
    public string[] DefaultTrackers { get; set; } = new string[] { };


    public const string CleanupSeconds_NAME = "CleanupSeconds";
    public double CleanupSeconds { get; set; } = 1200;


    public const string CleanupThresholdHours_NAME = "CleanupThresholdHours";
    public double CleanupThresholdHours { get; set; } = 0.2;


    public const string TrackerSetupSeconds_NAME = "TrackerSetupSeconds";
    public double TrackerSetupSeconds { get; set; } = 300;


    public const string AdvertiseSeconds_NAME = "AdvertiseSeconds";
    public double AdvertiseSeconds { get; set; } = 600;

    public const string PingPercent_NAME = "PingPercent";
    public double PingPercent { get; set; } = 30; //0-100
    public const string PingSeconds_NAME = "PingIntervalSeconds";
    public double PingIntervalSeconds { get; set; } = 15;

    public const string GossipIntervalSeconds_NAME = "GossipIntervalSeconds";
    public double GossipIntervalSeconds { get; set; } = 60;

    // maximum of gossips sent out at once
    public const string GossipCount_NAME = "GossipCount";
    public int GossipCount { get; set; } = 5;
    public const string GossipNewNodes_NAME = "GossipNewNodes";
    public bool GossipNewNodes { get; set; } = false;
   
    public const string PersistSeconds_NAME = "PersistSeconds";
    public double PersistSeconds { get; set; } = 600;

    public Dictionary<string, string> GetSettingForDb()
    {
        return new Dictionary<string, string>
        {
            { Port_NAME, Port.ToString() },
            { UpdateSeconds_NAME, UpdateSeconds.ToString() },
            { P2PRole_NAME, P2PRole.ToString() },
            { RelayTTL_NAME, RelayTTL.ToString() },
            { KNearestNodes_NAME, KNearestNodes.ToString() },
            { DefaultTrackers_NAME, JsonSerializer.Serialize(DefaultTrackers ?? new string[]{ }) },
            
            { CleanupSeconds_NAME, CleanupSeconds.ToString() },
            { CleanupThresholdHours_NAME, CleanupThresholdHours.ToString() },
            { TrackerSetupSeconds_NAME, TrackerSetupSeconds.ToString() },

            { AdvertiseSeconds_NAME, AdvertiseSeconds.ToString() },
            { PingPercent_NAME, PingPercent.ToString() },
            { PingSeconds_NAME, PingIntervalSeconds.ToString() },
            { GossipIntervalSeconds_NAME, GossipIntervalSeconds.ToString() },
            { GossipCount_NAME, GossipCount.ToString() },
            { GossipNewNodes_NAME, GossipNewNodes.ToString() },
            { PersistSeconds_NAME, PersistSeconds.ToString() }
        };
    }

    public static string GetPrefixed(string source, string prefix)
    {
        return !string.IsNullOrEmpty(prefix) && source.StartsWith(prefix) ? source : prefix.Trim(':') + ':' + source;
    }
    /*
     * for saving into IConfiguration
     **/
    public Dictionary<string, string> GetFlattenedSettings(string prefix = "")
    {
        var settings = new Dictionary<string, string>
        {
            { GetPrefixed(Port_NAME, prefix), Port.ToString() },
            { GetPrefixed(UpdateSeconds_NAME, prefix), UpdateSeconds.ToString() },
            { GetPrefixed(P2PRole_NAME, prefix), P2PRole.ToString() },
            { GetPrefixed(RelayTTL_NAME, prefix), RelayTTL.ToString() },
            { GetPrefixed(KNearestNodes_NAME, prefix), KNearestNodes.ToString() },

            { GetPrefixed(DefaultTrackers_NAME, prefix), JsonSerializer.Serialize(DefaultTrackers ?? new string[] { }) },

            { GetPrefixed(CleanupSeconds_NAME, prefix), CleanupSeconds.ToString() },
            { GetPrefixed(CleanupThresholdHours_NAME, prefix), CleanupThresholdHours.ToString() },
            { GetPrefixed(TrackerSetupSeconds_NAME, prefix), TrackerSetupSeconds.ToString() },

            { GetPrefixed(AdvertiseSeconds_NAME, prefix), AdvertiseSeconds.ToString() },
            { GetPrefixed(PingPercent_NAME, prefix), PingPercent.ToString() },
            { GetPrefixed(PingSeconds_NAME, prefix), PingIntervalSeconds.ToString() },
            { GetPrefixed(GossipIntervalSeconds_NAME, prefix), GossipIntervalSeconds.ToString() },
            { GetPrefixed(GossipCount_NAME, prefix), GossipCount.ToString() },
            { GetPrefixed(GossipNewNodes_NAME, prefix), GossipNewNodes.ToString() },
            { GetPrefixed(PersistSeconds_NAME, prefix), PersistSeconds.ToString() },
        };
        
        //PutFlatternedArray(settings, DefaultTrackers_NAME, DefaultTrackers);

        return settings;
    }

    //private void PutFlatternedArray(
    //    Dictionary<string, string> settings, 
    //    string defaultTrackers_NAME, string[] defaultTrackers)
    //{
    //    throw new NotImplementedException();
    //}

    public ISettings FromDbSettings(Dictionary<string, string> dict)
    {
        var settings = this;
        if (dict.TryGetValue(Port_NAME, out var port) && int.TryParse(port, out var portValue))
            settings.Port = portValue;
        if (dict.TryGetValue(UpdateSeconds_NAME, out var update) && double.TryParse(update, out var updateValue))
            settings.UpdateSeconds = updateValue;
        if (dict.TryGetValue(P2PRole_NAME, out var role) && Enum.TryParse<NodeRole>(role, out var roleValue))
            settings.P2PRole = roleValue;
        if (dict.TryGetValue(RelayTTL_NAME, out var ttl) && int.TryParse(ttl, out var ttlValue))
            settings.RelayTTL = ttlValue;
        if (dict.TryGetValue(KNearestNodes_NAME, out var knearest) && int.TryParse(knearest, out var knearestValue))
            settings.KNearestNodes = knearestValue;
        try
        {
            if (dict.TryGetValue(DefaultTrackers_NAME, out var trackers))
            {
                settings.DefaultTrackers = JsonSerializer.Deserialize<string[]>(trackers);
            }
            else
            {
                settings.DefaultTrackers = new string[] { };
            }
        }
        catch
        {
            settings.DefaultTrackers = new string[] { };
        }

        if (dict.TryGetValue(CleanupSeconds_NAME, out var cleanup) && double.TryParse(cleanup, out var cleanupValue))
            settings.CleanupSeconds = cleanupValue;
        if (dict.TryGetValue(CleanupThresholdHours_NAME, out var cleanupThreshold) && double.TryParse(cleanupThreshold, out var cleanupThresholdValue))
            settings.CleanupThresholdHours = cleanupThresholdValue;
        if (dict.TryGetValue(TrackerSetupSeconds_NAME, out var trackerSetup) && double.TryParse(trackerSetup, out var trackerSetupValue))
            settings.TrackerSetupSeconds = trackerSetupValue;


        if (dict.TryGetValue(AdvertiseSeconds_NAME, out var advertise) && double.TryParse(advertise, out var advertiseValue))
            settings.AdvertiseSeconds = advertiseValue;
        if (dict.TryGetValue(PingPercent_NAME, out var pingPercent) && double.TryParse(pingPercent, out var pingPercentValue))
            settings.PingPercent = pingPercentValue;
        if (dict.TryGetValue(PingSeconds_NAME, out var pingSeconds) && double.TryParse(pingSeconds, out var pingSecondsValue))
            settings.PingIntervalSeconds = pingSecondsValue;
        if (dict.TryGetValue(GossipIntervalSeconds_NAME, out var gossip) && double.TryParse(gossip, out var gossipValue))
            settings.GossipIntervalSeconds = gossipValue;
        if (dict.TryGetValue(GossipCount_NAME, out var count) && int.TryParse(count, out var countValue))
            settings.GossipCount = countValue;
        if (dict.TryGetValue(GossipNewNodes_NAME, out var newNodes) && bool.TryParse(newNodes, out var newNodesValue))
            settings.GossipNewNodes = newNodesValue;
        if (dict.TryGetValue(PersistSeconds_NAME, out var persist) && double.TryParse(persist, out var persistValue))
            settings.PersistSeconds = persistValue;


        return settings;
    }

    public void WriteToContextStore(ContextInstance inst)
    {
        inst.Config.Set(Port_NAME, Port, SECTION);
        inst.Config.Set(UpdateSeconds_NAME, UpdateSeconds, SECTION);
        inst.Config.Set(P2PRole_NAME, P2PRole, SECTION);
        inst.Config.Set(RelayTTL_NAME, RelayTTL, SECTION);
        inst.Config.Set(KNearestNodes_NAME, KNearestNodes, SECTION);
        inst.Config.Set(DefaultTrackers_NAME, DefaultTrackers, SECTION);

        inst.Config.Set(CleanupSeconds_NAME, CleanupSeconds, SECTION);
        inst.Config.Set(CleanupThresholdHours_NAME, CleanupThresholdHours, SECTION);
        inst.Config.Set(TrackerSetupSeconds_NAME, TrackerSetupSeconds, SECTION);

        inst.Config.Set(AdvertiseSeconds_NAME, AdvertiseSeconds, SECTION);
        inst.Config.Set(PingPercent_NAME, PingPercent, SECTION);
        inst.Config.Set(PingSeconds_NAME, PingIntervalSeconds, SECTION);
        inst.Config.Set(GossipIntervalSeconds_NAME, GossipIntervalSeconds, SECTION);
        inst.Config.Set(GossipCount_NAME, GossipCount, SECTION);
        inst.Config.Set(GossipNewNodes_NAME, GossipNewNodes, SECTION);
        inst.Config.Set(PersistSeconds_NAME, PersistSeconds, SECTION);
    }

    public bool IsSettingSensitive(string key) => false;
}