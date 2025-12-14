using System.Security.Cryptography;

using Common.Interfaces.Menu;

using Microsoft.AspNetCore.Identity;

using ProcessorApplication.Infrastructure;

namespace ProcessorApplication.Services;
public static class AesEncryptionHelper
{
    private const int KeySize = 32; // 256 bits
    private const int IvSize = 16;  // 128 bits

    /// <summary>
    /// Encrypts plain text using AES-256 and returns a Base64-encoded string 
    /// containing the IV concatenated with the ciphertext.
    /// </summary>
    /// <param name="plainText">The data to encrypt (e.g., user profile notes, PHSK).</param>
    /// <param name="keyBytes">The 32-byte symmetric key derived from PHSK/Server Hash.</param>
    /// <returns>Base64 string of IV + Ciphertext.</returns>
    public static string Encrypt(string plainText, byte[] keyBytes)
    {
        if (string.IsNullOrEmpty(plainText))
            return string.Empty;
        if (keyBytes == null || keyBytes.Length != KeySize)
            throw new ArgumentException($"Key must be exactly {KeySize} bytes (256 bits).", nameof(keyBytes));

        using Aes aesAlg = Aes.Create();
        aesAlg.Key = keyBytes;
        aesAlg.Mode = CipherMode.CBC; // Cipher Block Chaining (standard, secure mode)
        aesAlg.Padding = PaddingMode.PKCS7;

        // 1. Generate a random, unique IV for THIS encryption operation
        aesAlg.GenerateIV();
        byte[] iv = aesAlg.IV;

        // 2. Create the encryptor
        ICryptoTransform encryptor = aesAlg.CreateEncryptor(aesAlg.Key, iv);

        // 3. Perform the encryption
        using MemoryStream msEncrypt = new MemoryStream();
        using CryptoStream csEncrypt = new CryptoStream(msEncrypt, encryptor, CryptoStreamMode.Write);

        using (StreamWriter swEncrypt = new StreamWriter(csEncrypt))
        {
            swEncrypt.Write(plainText);
        }

        byte[] encryptedBytes = msEncrypt.ToArray();

        // 4. Combine IV and ciphertext for storage: [IV | Ciphertext]
        byte[] result = new byte[IvSize + encryptedBytes.Length];
        Buffer.BlockCopy(iv, 0, result, 0, IvSize);
        Buffer.BlockCopy(encryptedBytes, 0, result, IvSize, encryptedBytes.Length);

        return Convert.ToBase64String(result);
    }

    /// <summary>
    /// Decrypts a Base64-encoded string (IV + Ciphertext) using AES-256.
    /// </summary>
    /// <param name="cipherTextWithIv">Base64 string of IV + Ciphertext.</param>
    /// <param name="keyBytes">The 32-byte symmetric key used for encryption.</param>
    /// <returns>The decrypted plain text string.</returns>
    public static string Decrypt(string cipherTextWithIv, byte[] keyBytes)
    {
        if (string.IsNullOrEmpty(cipherTextWithIv))
            return string.Empty;
        if (keyBytes == null || keyBytes.Length != KeySize)
            throw new ArgumentException($"Key must be exactly {KeySize} bytes (256 bits).", nameof(keyBytes));

        byte[] fullCipherBytes;
        try
        {
            fullCipherBytes = Convert.FromBase64String(cipherTextWithIv);
        }
        catch (FormatException)
        {
            throw new CryptographicException("Input is not valid Base64.");
        }

        if (fullCipherBytes.Length < IvSize + 1) // IV + at least 1 byte of data
            throw new CryptographicException("Ciphertext is too short or invalid.");

        // 1. Separate IV and ciphertext
        byte[] iv = new byte[IvSize];
        byte[] cipherText = new byte[fullCipherBytes.Length - IvSize];
        Buffer.BlockCopy(fullCipherBytes, 0, iv, 0, IvSize);
        Buffer.BlockCopy(fullCipherBytes, IvSize, cipherText, 0, fullCipherBytes.Length - IvSize);

        using Aes aesAlg = Aes.Create();
        aesAlg.Key = keyBytes;
        aesAlg.IV = iv;
        aesAlg.Mode = CipherMode.CBC;
        aesAlg.Padding = PaddingMode.PKCS7;

        // 2. Create the decryptor
        ICryptoTransform decryptor = aesAlg.CreateDecryptor(aesAlg.Key, aesAlg.IV);

        // 3. Perform the decryption
        using MemoryStream msDecrypt = new MemoryStream(cipherText);
        using CryptoStream csDecrypt = new CryptoStream(msDecrypt, decryptor, CryptoStreamMode.Read);
        using StreamReader srDecrypt = new StreamReader(csDecrypt);

        try
        {
            return srDecrypt.ReadToEnd();
        }
        catch (CryptographicException ex)
        {
            // This typically means the key or IV was incorrect/tampered with
            throw new CryptographicException("Decryption failed. Key or ciphertext likely invalid.", ex);
        }
    }
}