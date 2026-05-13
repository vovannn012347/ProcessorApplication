using System.ComponentModel.DataAnnotations;

using ProviderlessModule.Infrastructure.Interfaces;

namespace ProviderlessModule.Configuration.Tunnel;
public class StaticUrlSettings : ITunnelSettings
{
    public string ProviderKey => "static";

    public string ProviderName => "StaticUrlSettings_key";
    public string SettingsViewPath => "~/Views/Providerless/Settings/_Settings_Tun_static.cshtml";

    [Required]
    [Display(Name = "Public Static URL")]
    public string PublicUrl { get; set; } = string.Empty;
}