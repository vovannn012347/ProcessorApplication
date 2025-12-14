using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

using ProcessorApplication.Models.Settings;
using ProcessorApplication.Models.User;

namespace ProcessorApplication.ViewModels.Account;

public class AdminRegisterViewModel
{
    [StringLength(100)] 
    public string Name { get; set; }
    [StringLength(100)]
    public string Surname { get; set; }
    [Required]
    [DataType(DataType.Password)] 
    public string Password { get; set; }
    [DataType(DataType.Password)]
    [Compare("Password")]
    public string ConfirmPassword { get; set; }


    [Description("It is required to set up email for notification of users. Preferably your work email. Cannot view user data without user notification.")]
    public string Email { get; set; }


}