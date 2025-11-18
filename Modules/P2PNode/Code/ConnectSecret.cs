using System.IO.Compression;
using System.Text;
using System.Text.Json;

using Common.Code;

using Microsoft.Extensions.DependencyInjection;

using static System.Net.Mime.MediaTypeNames;

namespace ProcessorApplication;

public static class ConnectSecret
{
    private static readonly string Alphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
    private static readonly Random Rng = new Random();

    public static string SerializeSecret(string message)
    {
        var mixed = new StringBuilder();

        foreach (char ch in message)
        {
            mixed.Append(ch);
            mixed.Append(Alphabet[Rng.Next(Alphabet.Length)]);
        }

        string mixedStr = mixed.ToString();
        byte[] mixedBytes = Encoding.UTF8.GetBytes(mixedStr);
        byte[] compressedBytes = Compression.Compress(mixedBytes);

        //string messageStr = Encoding.UTF8.GetString(compressedBytes);
        //byte[] jsonBytes = Compression.Decompress(compressedBytes);

        return Convert.ToBase64String(compressedBytes);

    }

    public static string DeserializeSecret(string encodedMessage)
    {
        byte[] compressedBytes = Convert.FromBase64String(encodedMessage);
        byte[] mixedBytes = Compression.Decompress(compressedBytes);
        string mixedStr = Encoding.UTF8.GetString(mixedBytes);

        var unmixed = new StringBuilder();

        // Pick every 2nd char (skip the random one)
        for (int i = 0; i < mixedStr.Length; i += 2)
        {
            unmixed.Append(mixedStr[i]);
        }

        return unmixed.ToString();
    }
}