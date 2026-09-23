using System.ComponentModel.DataAnnotations;

using ProcessorApplication.Configuration.Settings;
using ProcessorApplication.ViewModels.User;

namespace ProcessorApplication.ViewModels;

public class SettingsViewModel
{
    public SecuritySettings Security { get; set; } = new SecuritySettings();
    public EmailSettings Email { get; set; } = new EmailSettings();
    public RegisteringSettingsViewModel Registering { get; set; } = new RegisteringSettingsViewModel();
}

public class RegisteringSettingsViewModel : RegisteringSettings
{
    public List<RoleSelectionViewModel> DefaultRolesSelection { get; set; }

}
