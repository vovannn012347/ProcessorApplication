using System.ComponentModel.DataAnnotations;

using ProviderlessModule.Infrastructure.Interfaces;

namespace ProviderlessModule.Configuration.Tunnel;

//this is commented so reflection does not return those stuff for layout
//currently ngrok is WIP
//public class NgrokSettings : ITunnelSettings
//{
//    public string ProviderKey => "ngrok";

//    public string ProviderName => "NgrokSettings_key";
//    public string SettingsViewPath => "~/Views/Providerless/Settings/_Settings_Tun_ngrok.cshtml";
//    /// <summary>
//    /// Mandatory Authtoken from the Ngrok Dashboard.
//    /// </summary>
//    [Required]
//    public string AuthToken { get; set; }

//    /// <summary>
//    /// The edge region (e.g., 'us', 'eu', 'au', 'jp').
//    /// </summary>
//    public string Region { get; set; } = "us";

//    /// <summary>
//    /// Optional: If the clinic has a static domain assigned in Ngrok.
//    /// </summary>
//    public string CustomDomain { get; set; }

//    // OS-specific binary configs (using the same pattern as Cloudflare)
//    public BinaryPlatformConfig Windows { get; set; } = new();
//    public BinaryPlatformConfig Linux { get; set; } = new();

//    public class BinaryPlatformConfig
//    {
//        public string BinaryPath { get; set; }
//        public string DownloadUrl { get; set; }
//    }
//}
