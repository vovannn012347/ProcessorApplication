
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

}