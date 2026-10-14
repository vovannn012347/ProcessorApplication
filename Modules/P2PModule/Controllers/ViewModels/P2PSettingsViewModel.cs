
using System.Runtime.InteropServices;

using Common.Interfaces;

using P2PModule.Configuration;

namespace P2PModule.Controllers.ViewModels;

public class P2PSettingsViewModel
{
    public P2PSettings General { get; set; }
    public StorageSettings Storage { get; set; }
    public GossipSettings Gossip { get; set; }
    public RatingSettings Rating { get; set; }
    public IEnumerable<string> AvailableRoles { get; set; }

    /// <summary>Consensus channels installed by loaded modules - the ones that can be advertised.</summary>
    public IEnumerable<string> InstalledConsensusChannels { get; set; } = Array.Empty<string>();

    /// <summary>Opted-in channels whose module isn't loaded right now. Kept in settings (not dropped on
    /// save) so they come back automatically when the module does; shown read-only.</summary>
    public IEnumerable<string> UnavailableConsensusChannels { get; set; } = Array.Empty<string>();
    public IEnumerable<string> EndpointProvisions { get; set; }
}