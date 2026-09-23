
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
    public IEnumerable<string> EndpointProvisions { get; set; }
}