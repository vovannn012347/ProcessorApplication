namespace ProcessorApplication.Models.User;


public class ProfileExportModel
{
    public string ExportVersion { get; set; } = "1.0";
    public DateTime ExportedAt { get; set; } = DateTime.UtcNow;

    public string UserName { get; set; }
    public string Name { get; set; }
    public string Surname { get; set; }
    public string DisplayNickname { get; set; }

    // The "Keys to the Kingdom"
    // Decrypted PHSK by user password
    //it is not said directly in names of variables for security reasons
    public string PersonalHashKey { get; set; }
    // this is for password key correctness check
    // we do not see open key
    public string UserIdLockedByPHSK { get; set; }
}