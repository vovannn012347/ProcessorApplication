using System.ComponentModel.DataAnnotations;

using ProcessorApplication.Models.Settings;
using ProcessorApplication.Models.User;

namespace ProcessorApplication.ViewModels.Account;

public class ChangePasswordViewModel
{
    [Required]
    [DataType(DataType.Password)]
    public string CurrentPassword { get; set; }
    [Required]
    [DataType(DataType.Password)]
    public string NewPassword { get; set; }
    [DataType(DataType.Password)]
    [Compare("NewPassword")]
    public string ConfirmPassword { get; set; }
}
