using System;
using System.Security.Cryptography;
using System.Text;

namespace ProcessorApplication.Utils
{
    public static class Obfuscator
    {
        private static readonly byte[] Salt = Encoding.UTF8.GetBytes("MedicalSystem2025");

        public static string Obfuscate(string input)
        {
            var inputBytes = Encoding.UTF8.GetBytes(input);
            var outputBytes = new byte[inputBytes.Length];
            for (int i = 0; i < inputBytes.Length; i++)
            {
                outputBytes[i] = (byte)(inputBytes[i] ^ Salt[i % Salt.Length]);
            }
            return Convert.ToBase64String(outputBytes);
        }

        public static string Deobfuscate(string input)
        {
            var inputBytes = Convert.FromBase64String(input);
            var outputBytes = new byte[inputBytes.Length];
            for (int i = 0; i < inputBytes.Length; i++)
            {
                outputBytes[i] = (byte)(inputBytes[i] ^ Salt[i % Salt.Length]);
            }
            return Encoding.UTF8.GetString(outputBytes);
        }

        public static string GenerateUserHashId(string input)
        {
            using var sha256 = SHA256.Create();
            var bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(input + Salt));
            return Convert.ToBase64String(bytes);
        }
    }
}
