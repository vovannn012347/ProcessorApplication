using System.ComponentModel.DataAnnotations;

using ProcessorApplication.Database.Models;

namespace ProcessorApplication.ViewModels.User;

public class RoleSelectionViewModel
{
    public string RoleName { get; set; }
    public bool IsSelected { get; set; }
}


public class ProfileViewModel
{
    public ApplicationUser User { get; set; }
    public UserSecuritySettings? UserSecurity { get; set; }

    public List<RoleSelectionViewModel> RoleSelections { get; set; } = new List<RoleSelectionViewModel>();

    // We bind the decrypted string here for display/editing
    public string DecryptedSensitiveData { get; set; }

    public bool IsAuditView { get; set; } = false;
    public bool CanEditRoles { get; set; } = false;

    [DataType(DataType.Password)]
    [Display(Name = "Current Password")]
    public string? CurrentPassword { get; set; }

    [DataType(DataType.Password)]
    [Display(Name = "New Password")]
    public string? NewPassword { get; set; }

    [DataType(DataType.Password)]
    [Display(Name = "Confirm New Password")]
    [Compare("NewPassword", ErrorMessage = "The new password and confirmation password do not match.")]
    public string? ConfirmPassword { get; set; }
}