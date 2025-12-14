using System.Text;

using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;

using ProcessorApplication.Database;
using ProcessorApplication.Database.Models;
using ProcessorApplication.Services.User;

namespace ProcessorApplication.Services.HashStamps;

//normal custom key protector
public class ServerUserDEKprotectorService<TUser> : ILookupProtector
    where TUser : ApplicationUser, new()
{
    private readonly IDataProtector _dataProtector;
    private readonly AppDbContext _appDbContext;
    private readonly IAuditHashKeyProvider _securityKeyProvider; 
    private readonly UserKeyHolder _keyHolder;

    private const string Purpose = "ProtectedPersonalData";

    public ServerUserDEKprotectorService(
        IDataProtectionProvider dataProtectionProvider,
        AppDbContext appDbContext,
        IAuditHashKeyProvider securityKeyProvider,
        UserKeyHolder keyHolder)
    {
        // Create a specific protector instance for our data purpose
        _dataProtector = dataProtectionProvider.CreateProtector(Purpose);
        _appDbContext = appDbContext;
        _securityKeyProvider = securityKeyProvider;
        _keyHolder = keyHolder;
    }

    public string Protect(string key, string data)
    {
        ApplicationUser user = _appDbContext.Users.FirstOrDefault(u => u.Id == key);
        if (user == null)
        {
            return string.Empty;
        }

        string userSpecificKey = key;// GetUserSecretKey(key);

        // 2. Combine the User Key with the Server-Side Protected Data
        byte[] dataBytes = Encoding.UTF8.GetBytes(data);
        byte[] userKeyBytes = Encoding.UTF8.GetBytes(userSpecificKey);

        // Concatenate the user key bytes with the data bytes
        byte[] compositeBytes = new byte[userKeyBytes.Length + dataBytes.Length];
        System.Buffer.BlockCopy(userKeyBytes, 0, compositeBytes, 0, userKeyBytes.Length);
        System.Buffer.BlockCopy(dataBytes, 0, compositeBytes, userKeyBytes.Length, dataBytes.Length);

        // 3. Encrypt the composite using the Server Key
        byte[] encryptedBytes = _dataProtector.Protect(compositeBytes);

        return Convert.ToBase64String(encryptedBytes);
    }

    // --- DECRYPTION METHOD ---
    public string Unprotect(string key, string protectedData)
    {
        // 1. Get the User Object using the key (which is the UserId)
        ApplicationUser user = _appDbContext.Users.FirstOrDefault(u => u.Id == key);
        if (user == null)
        {
            return string.Empty;
        }

        // Retrieve the user-specific key again
        string userSpecificKey = key;// GetUserSecretKey(user);
        byte[] userKeyBytes = Encoding.UTF8.GetBytes(userSpecificKey);
        int userKeyLength = userKeyBytes.Length;

        // 2. Decrypt the protected data using the Server Key
        byte[] encryptedBytes = Convert.FromBase64String(protectedData);
        byte[] decryptedCompositeBytes = _dataProtector.Unprotect(encryptedBytes);

        // Check if the decrypted length is sufficient
        if (decryptedCompositeBytes.Length <= userKeyLength)
        {
            // Error handling for corrupted or invalid data
            return string.Empty;
        }

        // 3. Verify and strip the User Key prefix from the composite data
        // For a true composite encryption, you'd perform a HMAC or decryption step here.
        // For this example, we'll verify the prefix before stripping it.
        byte[] prefix = new byte[userKeyLength];
        System.Buffer.BlockCopy(decryptedCompositeBytes, 0, prefix, 0, userKeyLength);

        if (Encoding.UTF8.GetString(prefix) != userSpecificKey)
        {
            // Security failure: the prefix doesn't match the expected user key
            throw new System.Security.SecurityException("Composite key validation failed during unprotection.");
        }

        // 4. Extract the original data bytes
        byte[] dataBytes = new byte[decryptedCompositeBytes.Length - userKeyLength];
        System.Buffer.BlockCopy(decryptedCompositeBytes, userKeyLength, dataBytes, 0, dataBytes.Length);

        return Encoding.UTF8.GetString(dataBytes);
    }
}
