namespace ProcessorApplication.Services.User;

public class UserKeyHolder
{
    // Stores the temporary decrypted key during registration/login
    public string? DecryptedUserHashKey { get; set; }
}