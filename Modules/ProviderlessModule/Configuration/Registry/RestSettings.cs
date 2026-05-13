using System.ComponentModel.DataAnnotations;

using ProviderlessModule.Infrastructure.Interfaces;

namespace ProviderlessModule.Configuration.Registry;


//this is commented so reflection does not return those stuff for layout
//currently this is is WIP
//public class RestSettings : IRegistrySettings
//{
//    public string ProviderKey => "rest";

//    public string ProviderName => "RestSettings_key";
//    public string SettingsViewPath => "~/Views/Providerless/Settings/_Settings_Reg_rest.cshtml";

//    [Required]
//    [Display(Name = "Spreadsheet ID")]
//    public string Url { get; set; } // Url or ip adress for rest-based redirection site

//    [Required]
//    [Display(Name = "Service Account Key JSON")]
//    public string SecurityToken { get; set; } // Connection Security Token
//}
