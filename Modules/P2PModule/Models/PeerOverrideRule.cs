using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace P2PModule.Models;

/// <summary>
/// Represents an admin-driven trust modification profile.
/// </summary>
public class PeerOverrideRule
{
    public string NodeId { get; set; } = string.Empty;
    public bool ForceWhitelist { get; set; } // Bypass reputation system entirely
    public bool ForceBlacklist { get; set; } // Force drop and block
    public int? OverrideTrustScore { get; set; } // Explicit forced rating
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string AdminNotes { get; set; } = string.Empty;
}