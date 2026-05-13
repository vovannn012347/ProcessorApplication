using System.ComponentModel.DataAnnotations;

using ProviderlessModule.Infrastructure.Interfaces;

namespace ProviderlessModule.Configuration.Registry;

public class NoneRegistrySettings : IRegistrySettings
{
    public string ProviderKey => "none";

    public string ProviderName => "None_key";
    public string SettingsViewPath => "~/Views/Providerless/Settings/_Settings_Reg_none.cshtml";
}
