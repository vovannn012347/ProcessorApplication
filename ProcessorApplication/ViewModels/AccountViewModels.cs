using System.ComponentModel.DataAnnotations;

using ProcessorApplication.Models.Settings;
using ProcessorApplication.Models.User;

namespace ProcessorApplication.ViewModels;
/*
public class RegisterViewModel
{
    [Required(ErrorMessage = "Name is required.")]
    [StringLength(100)] 
    public string Name { get; set; }
    [Required(ErrorMessage = "Surname is required.")]
    [StringLength(100)]
    public string Surname { get; set; }
    [Required]
    [DataType(DataType.Password)] 
    public string Password { get; set; }
    [DataType(DataType.Password)]
    [Compare("Password")]
    public string ConfirmPassword { get; set; }
    [StringLength(50)] 
    public string DisplayNickname { get; set; }
}

public class LoginViewModel
{
    [Required] 
    public string UserName { get; set; }
    [Required]
    [DataType(DataType.Password)] 
    public string Password { get; set; }
    //public bool RememberMe { get; set; }
}

public class ChangePasswordViewModel
{
    [Required]
    [DataType(DataType.Password)] 
    public string CurrentPassword { get; set; }
    [Required]
    [DataType(DataType.Password)] 
    public string NewPassword { get; set; }
    [DataType(DataType.Password)][Compare("NewPassword")] 
    public string ConfirmPassword { get; set; }
}

public class ProfileViewModel
{
    public ApplicationUser User { get; set; }
    public string DecryptedSensitiveData { get; set; }
    public IList<string> Roles { get; set; } = new List<string>();
    public bool IsAuditView { get; set; } = false;
}

public class ProfileExportModel
{
    public string UserName { get; set; }
    public string Name { get; set; }
    public string Surname { get; set; }
    public string DisplayNickname { get; set; }
    public string HashSignKey { get; set; }
    public string UserIdLockedByPHSK { get; set; }
}*/