
using System.Security.Cryptography;
using System.Text;

namespace ProcessorApplication.Services.User;

public class SecurityHelperUtil
{
    public static string GeneratePHSK(int length = 32)
    {
        return Convert.ToBase64String(RandomNumberGenerator.GetBytes(length));
    }

    public static byte[] MakeValidHashKey(string inputKey)
    {
        if (string.IsNullOrEmpty(inputKey)) throw new ArgumentNullException(nameof(inputKey));

        using (var sha256 = SHA256.Create())
        {
            // Convert string to bytes
            var inputBytes = Encoding.UTF8.GetBytes(inputKey);

            // Hash produces exactly 32 bytes (256 bits)
            var hashBytes = sha256.ComputeHash(inputBytes);

            // Return as Base64 string for storage/usage in encryption helper
            return hashBytes;// Convert.ToBase64String(hashBytes);
        }
    }
    public static string DeriveKey(string userPhsk, string masterKey)
    {
        var keyBytes = Encoding.UTF8.GetBytes(masterKey);
        var messageBytes = Encoding.UTF8.GetBytes(userPhsk);

        using (var hmac = new HMACSHA256(keyBytes))
        {
            var hash = hmac.ComputeHash(messageBytes);

            return Convert.ToBase64String(hash);
        }
    }

    public static string EncryptData(string data, string key)
    {
        var keyDerived = MakeValidHashKey(key);
        return AesEncryptionHelper.Encrypt(data, keyDerived);
    }
    public static string DecryptData(string data, string key)
    {
        var keyDerived = MakeValidHashKey(key);
        return AesEncryptionHelper.Decrypt(data, keyDerived);
    }

    //for phsk key saving
    public static string GenerateIdentityProofToken(string userPhsk, string userName)
    {
        if (string.IsNullOrEmpty(userPhsk)) throw new ArgumentNullException(nameof(userPhsk));
        if (string.IsNullOrEmpty(userName)) throw new ArgumentNullException(nameof(userName));

        // Use the PHSK as the HMAC key for proof of possession
        var keyBytes = Encoding.UTF8.GetBytes(userPhsk);
        var messageBytes = Encoding.UTF8.GetBytes(userName);

        using (var hmac = new HMACSHA256(keyBytes))
        {
            var hash = hmac.ComputeHash(messageBytes);
            return Convert.ToBase64String(hash);
        }
    }
    public static byte[] DeriveUnlockingKeyFromPassword(
        string password,
        string salt,
        int iterations,
        int keyLength = 32)
    {
        if (string.IsNullOrEmpty(password) || string.IsNullOrEmpty(salt))
            throw new ArgumentNullException("Password and salt cannot be null or empty.");

        byte[] saltBytes = Convert.FromBase64String(salt);
        byte[] passwordBytes = Encoding.UTF8.GetBytes(password);

        using var pbkdf2 = new Rfc2898DeriveBytes(
            passwordBytes,
            saltBytes,
            iterations,
            HashAlgorithmName.SHA256
        );

        return pbkdf2.GetBytes(keyLength);
    }

    public static string GenerateSalt(int length = 16)
    {
        using var rng = new RNGCryptoServiceProvider();
        byte[] saltBytes = new byte[length];
        rng.GetBytes(saltBytes);
        return Convert.ToBase64String(saltBytes);
    }
}