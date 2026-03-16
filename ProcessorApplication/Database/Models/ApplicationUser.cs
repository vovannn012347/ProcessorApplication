using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;

using Microsoft.AspNetCore.Identity;

using ProcessorApplication.Models.User;
using ProcessorApplication.Services;
using ProcessorApplication.Services.User;

namespace ProcessorApplication.Database.Models;

public class ApplicationUser : IdentityUser, IUserEncryptedData
{
    [PersonalData]
    public override string UserName { get; set; }

    [ProtectedPersonalData]
    public override string Email { get; set; }
    [ProtectedPersonalData]
    public override string NormalizedEmail { get; set; }

    [Required]
    [StringLength(100)]
    [ProtectedPersonalData]
    public string Name { get; set; } = string.Empty;

    [Required]
    [StringLength(100)]
    [ProtectedPersonalData]
    public string Surname { get; set; } = string.Empty;

    //preferred nickname for user to use, will display random-generated UserName to user
    [StringLength(50)]
    public string DisplayNickname { get; set; } = string.Empty;

    //user-encrypted hash key - to use when user logs in with password
    public DateTime CreateDateTime { get; set; } = DateTime.UtcNow;
    public DateTime LastLogin { get; set; } = DateTime.MinValue;

    
    [Required]
    public string PersonalHashKeyLockedByPassword { get; set; } = string.Empty; // this field is locked by user password
    //when this one is used - it is should be logged if security settings are enabled
    [Required]
    public string ServerEncryptedHashKey { get; set; } = string.Empty; //this is locked by correct server hash key

    [Required]
    public string UserIdLockedByPHSK { get; set; } = string.Empty; //this is locked by user encryption key

    [PersonalData]
    public string EncryptedSecuritySettings { get; set; } = string.Empty;

    //decrypted temporary hash key storage, isEncrpted persisted to database for... data presistence reasons
    public bool IsEncrypted { get; set; } = true;
    [NotMapped]
    [StringLength(32)]
    public string EncryptionHash { get; set; } = string.Empty; //user-interface key

    [NotMapped]
    public UserSecuritySettings SecurityPreferences
    {
        get
        {
            if (!string.IsNullOrEmpty(EncryptedSecuritySettings) && 
                !string.IsNullOrEmpty(EncryptionHash))
            {
                return JsonSerializer.Deserialize<UserSecuritySettings>(
                    AesEncryptionHelper.Decrypt(EncryptedSecuritySettings, SecurityHelperUtil.MakeValidHashKey(EncryptionHash))
                    ) ?? null;
            }
            return new UserSecuritySettings();
        }
        set
        {
            if(!string.IsNullOrEmpty(EncryptionHash))
                EncryptedSecuritySettings =
                    AesEncryptionHelper.Encrypt(JsonSerializer.Serialize(value),
                    SecurityHelperUtil.MakeValidHashKey(EncryptionHash));
        }
    }
}

public class UserSecuritySettings
{
    // Example: Notify user if an admin decrypts their profile data
    [Display(Name = "Notify on Profile Decryption/Access from anyone except you")]
    public bool NotifyOnDataAccess { get; set; } = true;
}