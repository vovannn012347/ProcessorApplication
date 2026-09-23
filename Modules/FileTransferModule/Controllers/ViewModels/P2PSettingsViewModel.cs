
using System.Runtime.InteropServices;

using Common.Interfaces;

using P2PModule.Configuration;

namespace FileSharingModule.Controllers.ViewModels;

public class P2PSettingsViewModel
{
    public P2PSettings General { get; set; }
    public StorageSettings Storage { get; set; }
    public GossipSettings Gossip { get; set; }
    public RatingSettings Rating { get; set; }
    public IEnumerable<string> EndpointProvisions { get; set; }
}