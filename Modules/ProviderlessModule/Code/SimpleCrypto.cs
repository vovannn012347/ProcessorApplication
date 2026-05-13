using System.ComponentModel.DataAnnotations;
using System.Linq.Expressions;
using System.Security.Cryptography;
using System.Text;

namespace ProviderlessModule.Code;

public static class SimpleCrypto
{
    private const int NonceSize = 12; // GCM standard nonce length
    private const int TagSize = 16;   // Standard 128-bit authentication tag

    public static string Encrypt(string plainText, string passkey)
    {
        if (string.IsNullOrWhiteSpace(plainText)) return string.Empty;

        // 1. Key Derivation: SHA256 = 32 bytes = AES-256 Key
        byte[] key = SHA256.HashData(Encoding.UTF8.GetBytes(passkey));

        byte[] nonce = new byte[NonceSize];
        RandomNumberGenerator.Fill(nonce);

        byte[] plainBytes = Encoding.UTF8.GetBytes(plainText);
        byte[] cipherText = new byte[plainBytes.Length];
        byte[] tag = new byte[TagSize];

        // 2. Perform Encryption
        using var aes = new AesGcm(key);
        aes.Encrypt(nonce, plainBytes, cipherText, tag);

        // 3. Pack: Nonce(12) + Tag(16) + CipherText(n)
        // Matches Python: final_blob = nonce + tag + ciphertext
        // Matches JS: raw.slice(0, 12), raw.slice(12, 28), raw.slice(28)
        byte[] result = new byte[NonceSize + TagSize + cipherText.Length];
        Buffer.BlockCopy(nonce, 0, result, 0, NonceSize);
        Buffer.BlockCopy(tag, 0, result, NonceSize, TagSize);
        Buffer.BlockCopy(cipherText, 0, result, NonceSize + TagSize, cipherText.Length);

        return Convert.ToBase64String(result);
    }

    public static string Decrypt(string base64Data, string passkey)
    {
        if (string.IsNullOrWhiteSpace(base64Data)) return string.Empty;

        try
        {
            byte[] rawData = Convert.FromBase64String(base64Data);

            // Defensive Check: Must at least contain Nonce + Tag
            if (rawData.Length < NonceSize + TagSize) return string.Empty;

            byte[] key = SHA256.HashData(Encoding.UTF8.GetBytes(passkey));

            // 1. Unpack based on established offsets
            byte[] nonce = rawData[..NonceSize];
            byte[] tag = rawData[NonceSize..(NonceSize + TagSize)];
            byte[] cipherText = rawData[(NonceSize + TagSize)..];

            byte[] plainBytes = new byte[cipherText.Length];

            // 2. Perform Decryption
            using var aes = new AesGcm(key);
            aes.Decrypt(nonce, cipherText, tag, plainBytes);

            return Encoding.UTF8.GetString(plainBytes);
        }
        catch
        {
            // Fail silently to prevent leaking information about the encryption failure
            return string.Empty;
        }
    }
}