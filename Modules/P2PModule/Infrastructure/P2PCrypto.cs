using System.Security.Cryptography;
using System.Text;

namespace P2PModule.Infrastructure;

//symmetric crypto engine that should always encrypt or decrypt data, disregarding anyhing
public static class P2PCrypto
{
    public static byte[] DeriveSeed(string masterKey, string purpose)
    {
        // Guard against null strings to prevent runtime argument exceptions
        masterKey ??= string.Empty;
        purpose ??= string.Empty;

        byte[] masterBytes = Encoding.UTF8.GetBytes(masterKey);
        byte[] purposeBytes = Encoding.UTF8.GetBytes(purpose);

        // Fixed salt to bind the HKDF extract phase to our P2P namespace
        byte[] salt = Encoding.UTF8.GetBytes("P2PModule_V1_Salt");

        // Derive exactly 32 bytes (256 bits) suitable for NIST P-256 scalar D
        return HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            ikm: masterBytes,
            outputLength: 32,
            salt: salt,
            info: purposeBytes
        );
    }

    public static string CrCompliantStr(string source) => 
        Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(source));
    public static string CrCompliantStrDecode(string source) =>
        System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(source));

    public static void DeriveSignaturePairFromSeed(byte[] seed, out byte[] certificate, out byte[] verify)
    {
        if (seed == null || seed.Length != 32)
            throw new ArgumentException("Seed must be exactly 32 bytes for P-256 derivation.");

        // --- SEC1 ASN.1 DER STRUCTURE CONSTRUCTION ---
        // Wraps the raw private scalar so the cryptographic engine automatically calculates public coordinates Q
        byte[] der = new byte[51];
        der[0] = 0x30; // Sequence
        der[1] = 0x31; // Length of payload (49 bytes)
        der[2] = 0x02; // Integer
        der[3] = 0x01; // Length (1)
        der[4] = 0x01; // Value (1)
        der[5] = 0x04; // Octet String
        der[6] = 0x20; // Length (32)
        Buffer.BlockCopy(seed, 0, der, 7, 32);
        der[39] = 0xA0; // Context-Specific Named Curve Parameter Tag [0]
        der[40] = 0x0A; // Length (10)
        der[41] = 0x06; // Object Identifier (OID)
        der[42] = 0x08; // Length (8)

        // OID: 1.2.840.10045.3.1.7 (NIST P-256 Named Curve Root Anchor)
        byte[] p256Oid = new byte[] { 0x2A, 0x86, 0x48, 0xCE, 0x3D, 0x03, 0x01, 0x07 };
        Buffer.BlockCopy(p256Oid, 0, der, 43, 8);

        using var ecdsa = ECDsa.Create();
        ecdsa.ImportECPrivateKey(der, out _);

        // Both keys are now cryptographically linked and correct
        certificate = ecdsa.ExportECPrivateKey();
        verify = ecdsa.ExportSubjectPublicKeyInfo();
    }


    /// <summary>
    /// Signs a string hash using the daily ECDsa private key.
    /// </summary>
    public static byte[] SignData(string hashData, byte[] privateKey) =>
        SignData(Encoding.UTF8.GetBytes(hashData), privateKey);

    public static byte[] SignData(byte[] hashData, byte[] privateKey)
    {
        using var ecdsa = ECDsa.Create();
        // Import the EC private key (exported via ExportECPrivateKey)
        ecdsa.ImportECPrivateKey(privateKey, out _);

        byte[] signatureBytes = ecdsa.SignData(hashData, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);

        return signatureBytes;
    }

    /// <summary>
    /// Verifies a signature against the provided public key (SubjectPublicKeyInfo).
    /// </summary>

    public static bool TestSignature(string hashData, byte[] publicKeyBytes, byte[] signatureBytes) =>
        TestSignature(Encoding.UTF8.GetBytes(hashData), publicKeyBytes, signatureBytes);

    public static bool TestSignature(byte[] hashData, byte[] publicKeyBytes, byte[] signatureBytes)
    {
        using var ecdsa = ECDsa.Create();
        // Import the SPKI public key (exported via ExportSubjectPublicKeyInfo)
        ecdsa.ImportSubjectPublicKeyInfo(publicKeyBytes, out _);

        return ecdsa.VerifyData(hashData, signatureBytes, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
    }

    /// <summary>
    /// Deterministically derives a matching, cryptographically valid public/private keypair from a 32-byte seed.
    /// Safely autocomputes the public point coordinates (Q) to prevent decryption mismatch errors.
    /// </summary>
    /// <param name="seed">A cryptographically secure 32-byte seed representing the private key scalar.</param>
    /// <param name="privateKey">Output: Standard exported SEC1 Private Key bytes.</param>
    /// <param name="publicKey">Output: Standard exported SubjectPublicKeyInfo (SPKI) bytes.</param>
    public static void DeriveKeyPairFromSeed(byte[] seed, out byte[] privateKey, out byte[] publicKey)
    {
        if (seed == null || seed.Length != 32)
            throw new ArgumentException("Seed must be exactly 32 bytes for P-256 derivation.", nameof(seed));

        // Construct a raw SEC1 EC Private Key ASN.1 structure to force CNG/OpenSSL to calculate the public key point Q
        byte[] der = new byte[51];
        der[0] = 0x30; // Sequence
        der[1] = 0x31; // Length of payload (49 bytes)
        der[2] = 0x02; // Integer
        der[3] = 0x01; // Length (1)
        der[4] = 0x01; // Value (1)
        der[5] = 0x04; // Octet String
        der[6] = 0x20; // Length (32)
        Buffer.BlockCopy(seed, 0, der, 7, 32);
        der[39] = 0xA0; // Context-Specific [0]
        der[40] = 0x0A; // Length (10)
        der[41] = 0x06; // Object Identifier (OID)
        der[42] = 0x08; // Length (8)

        // OID: 1.2.840.10045.3.1.7 (P-256 Curve)
        byte[] p256Oid = new byte[] { 0x2A, 0x86, 0x48, 0xCE, 0x3D, 0x03, 0x01, 0x07 };
        Buffer.BlockCopy(p256Oid, 0, der, 43, 8);

        using var ecdh = ECDiffieHellman.Create();
        ecdh.ImportECPrivateKey(der, out _);

        privateKey = ecdh.ExportECPrivateKey();
        publicKey = ecdh.ExportSubjectPublicKeyInfo();
    }

    /// <summary>
    /// Effortlessly encrypts a nonce using the recipient's static Public Key.
    /// </summary>
    /// <param name="nonce">The raw byte payload to encrypt (typically a 32-byte nonce).</param>
    /// <param name="recipientPublicKey">The static public key bytes of the target node.</param>
    /// <returns>A compact self-contained encrypted envelope containing [EphemeralPubKey] + [IV] + [Ciphertext].</returns>
    public static byte[] Encrypt(byte[] nonce, byte[] recipientPublicKey)
    {
        if (nonce == null) throw new ArgumentNullException(nameof(nonce));
        if (recipientPublicKey == null) throw new ArgumentNullException(nameof(recipientPublicKey));

        // 1. Generate an ephemeral, one-time EC key
        using var ephemeral = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        byte[] ephemeralPubKey = ephemeral.ExportSubjectPublicKeyInfo();

        // 2. Import recipient's public key
        using var recipient = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        recipient.ImportSubjectPublicKeyInfo(recipientPublicKey, out _);

        // 3. Derive symmetric key via ECDH (using SHA256 KDF by default)
        byte[] aesKey = ephemeral.DeriveKeyMaterial(recipient.PublicKey);

        // 4. Encrypt the nonce using AES-256-CBC
        byte[] iv = new byte[16];
        RandomNumberGenerator.Fill(iv);

        using var aes = Aes.Create();
        aes.Key = aesKey;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;

        using var encryptor = aes.CreateEncryptor();
        byte[] ciphertext = encryptor.TransformFinalBlock(nonce, 0, nonce.Length);

        // 5. Serialize everything into a single binary payload
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);
        writer.Write(ephemeralPubKey.Length);
        writer.Write(ephemeralPubKey);
        writer.Write(iv);
        writer.Write(ciphertext.Length);
        writer.Write(ciphertext);

        return ms.ToArray();
    }

    /// <summary>
    /// Effortlessly decrypts an envelope using your private key.
    /// </summary>
    /// <param name="envelope">The self-contained envelope returned by the Encrypt method.</param>
    /// <param name="recipientPrivateKey">Your private key bytes.</param>
    /// <returns>The decrypted raw nonce bytes.</returns>
    public static byte[] Decrypt(byte[] envelope, byte[] recipientPrivateKey)
    {
        if (envelope == null) throw new ArgumentNullException(nameof(envelope));
        if (recipientPrivateKey == null) throw new ArgumentNullException(nameof(recipientPrivateKey));

        // 1. Deserialize the envelope
        using var ms = new MemoryStream(envelope);
        using var reader = new BinaryReader(ms);

        int ephemeralPubKeyLen = reader.ReadInt32();
        byte[] ephemeralPubKey = reader.ReadBytes(ephemeralPubKeyLen);
        byte[] iv = reader.ReadBytes(16);
        int ciphertextLen = reader.ReadInt32();
        byte[] ciphertext = reader.ReadBytes(ciphertextLen);

        // 2. Reconstruct private key context
        using var localPrivate = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        localPrivate.ImportECPrivateKey(recipientPrivateKey, out _);

        // 3. Import sender's ephemeral public key
        using var ephemeral = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        ephemeral.ImportSubjectPublicKeyInfo(ephemeralPubKey, out _);

        // 4. Re-derive key material
        byte[] aesKey = localPrivate.DeriveKeyMaterial(ephemeral.PublicKey);

        // 5. Decrypt using AES-256-CBC
        using var aes = Aes.Create();
        aes.Key = aesKey;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC; 
        aes.Padding = PaddingMode.None;

        using var decryptor = aes.CreateDecryptor();
        return decryptor.TransformFinalBlock(ciphertext, 0, ciphertext.Length);
    }
}