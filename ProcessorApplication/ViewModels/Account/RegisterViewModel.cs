using System.ComponentModel.DataAnnotations;

using ProcessorApplication.Models.Settings;
using ProcessorApplication.Models.User;

namespace ProcessorApplication.ViewModels.Account;

public class RegisterViewModel
{
    [Required(ErrorMessage = "Name is required.")]
    [StringLength(100)] 
    public string Name { get; set; }
    [Required(ErrorMessage = "Surname is required.")]
    [StringLength(100)]
    public string Surname { get; set; }
    public string Email { get; set; }
    [Required]
    [DataType(DataType.Password)] 
    public string Password { get; set; }
    [DataType(DataType.Password)]
    [Compare("Password")]
    public string ConfirmPassword { get; set; }
    [StringLength(50)] 
    public string DisplayNickname { get; set; }
}