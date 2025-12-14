using System.ComponentModel.DataAnnotations;

using ProcessorApplication.Models.Settings;

namespace ProcessorApplication.ViewModels;

public class SettingsViewModel
{
    public SecuritySettings Security { get; set; } = new SecuritySettings();
    public EmailSettings Email { get; set; } = new EmailSettings();
}