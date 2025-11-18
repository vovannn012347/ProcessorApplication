using System.Text;

using Common.Interfaces;

namespace Common.Code;
public static class Encryption
{
    public static string Encrypt(string value)
    {
        // Placeholder: Implement AES encryption
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
    }

    public static string Decrypt(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        // Placeholder: Implement AES decryption
        return Encoding.UTF8.GetString(Convert.FromBase64String(value));
    }
}