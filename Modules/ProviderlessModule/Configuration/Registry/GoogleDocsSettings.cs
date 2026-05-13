using System.ComponentModel.DataAnnotations;

using ProviderlessModule.Infrastructure.Interfaces;

namespace ProviderlessModule.Configuration.Registry;

//this is commented so reflection does not return those stuff for layout
//currently this is is WIP
//public class GoogleDocsSettings : IRegistrySettings
//{
//    public string ProviderKey => "googledocs";

//    public string ProviderName => "GoogleDocsSettings_key";
//    public string SettingsViewPath => "~/Views/Providerless/Settings/_Settings_Reg_googledocs.cshtml";

//    [Required]
//    [Display(Name = "Spreadsheet ID")]
//    public string SpreadsheetId { get; set; } // Found in the Sheet's URL

//    [Required]
//    [Display(Name = "Service Account Key JSON")]
//    public string ServiceAccountJson { get; set; } // The contents of .json key
//}
