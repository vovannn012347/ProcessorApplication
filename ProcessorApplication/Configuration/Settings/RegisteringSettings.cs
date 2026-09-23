using System.ComponentModel.DataAnnotations;

namespace ProcessorApplication.Configuration.Settings;

public class RegisteringSettings
{
    [Display(Name = "Default roles for new users")]
    public List<string> DefaultRoles { get; set; } = new List<string> { "User" };
}