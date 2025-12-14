using System.ComponentModel.DataAnnotations;

using ProcessorApplication.Models.Settings;
using ProcessorApplication.Models.User;

namespace ProcessorApplication.ViewModels.Account;

public class LoginViewModel
{
    public string ReturnUrl { get; set; }
    [Required] 
    public string UserName { get; set; }
    [Required]
    [DataType(DataType.Password)] 
    public string Password { get; set; }
    //public bool RememberMe { get; set; }
}
