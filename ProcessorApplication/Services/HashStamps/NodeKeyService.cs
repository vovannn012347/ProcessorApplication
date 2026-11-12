using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Common.Infrastructure;

using Microsoft.Extensions.Options;

using ProcessorApplication.Configuration.Settings;

namespace ProcessorApplication.Services.HashStamps;

/// <summary>
/// The node root key: 32 random bytes in a file outside the database, created automatically on first start.
/// Same on Windows and Linux (no OS key store). Nobody has to type or manage anything.
///
/// Everything secret on the node hangs off this key:
///   - period (server) keys are derived from it: HMAC(root, stamp time), so they are never stored;
///   - old random server keys are stored encrypted with it.
/// A copy of the database or of the stamp backups alone reveals no keys. Back up this file together
/// with the stamp backups: root file + backups are enough to rebuild every key.
/// </summary>
public interface INodeKeyService
{
    /// <summary>Short public fingerprint of the root key (safe to show and store).</summary>
    string KeyId { get; }

    /// <summary>Full path of the root key file.</summary>
    string KeyFilePath { get; }

    /// <summary>
    /// Loads the root key, creating it if none exists and <paramref name="allowCreate"/> is true.
    /// When keys already depend on it and the file is gone, it is restored from a backup copy if one exists;
    /// otherwise this throws: a new key would silently invalidate every existing key.
    /// </summary>
    void EnsureLoaded(bool allowCreate);

    /// <summary>Server key for a stamp time, as the same 64-hex string the old random keys used.</summary>
    string DerivePeriodKey(DateTime stampTime);

    string Wrap(string plaintext);
    string Unwrap(string wrapped);

    /// <summary>
    /// A 32-byte key derived from the root key for encrypting one store of the node's own files
    /// (e.g. "keychain" for the server key chain). Different purposes get unrelated keys.
    /// </summary>
    byte[] DeriveStorageKey(string purpose);

    /// <summary>Copies the root key file into a backup folder (only if it isn't there yet or differs).</summary>
    void CopyKeyFileTo(string directory);
}

public sealed class NodeKeyService : INodeKeyService
{
    public const string DefaultRelativePath = "Module_Data/Main/keys/node.key";
    private const string KeyFileName = "node.key";
    private const string DerivationLabel = "processor-node/period-key/v1|";
    private const string WrapLabel = "processor-node/wrap-key/v1";
    private const string WrapPrefix = "g1:";

    private readonly IOptionsMonitor<SecuritySettings> _settings;
    private readonly ILogger<NodeKeyService> _logger;
    private readonly object _lock = new();
    private byte[]? _root;
    private byte[]? _wrapKey;

    public NodeKeyService(IOptionsMonitor<SecuritySettings> settings, ILogger<NodeKeyService> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    private sealed class KeyFile
    {
        public int Version { get; set; } = 1;
        public DateTime Created { get; set; }
        public string Key { get; set; } = "";
        public string Check { get; set; } = "";
    }

    public string KeyFilePath
    {
        get
        {
            string configured = _settings.CurrentValue.NodeKeyFilePath;
            string path = string.IsNullOrWhiteSpace(configured) ? DefaultRelativePath : configured;
            // The node's own folder (node home), never the shared program folder: two nodes started from one
            // program folder must not share a root key, and replacing the program files must not lose it.
            return NodeHome.Resolve(path);
        }
    }

    public string KeyId
    {
        get
        {
            var root = Root;
            return Convert.ToHexString(SHA256.HashData(root))[..16].ToLowerInvariant();
        }
    }

    private byte[] Root
    {
        get
        {
            if (_root == null) EnsureLoaded(allowCreate: false);
            return _root!;
        }
    }

    public void EnsureLoaded(bool allowCreate)
    {
        lock (_lock)
        {
            if (_root != null) return;

            string path = KeyFilePath;
            if (File.Exists(path))
            {
                _root = ReadKeyFile(path);
            }
            else if (TryRestoreFromBackup(path))
            {
                _root = ReadKeyFile(path);
                _logger.LogWarning("Node root key was missing and has been restored from the backup copy to {Path}", path);
            }
            else if (allowCreate)
            {
                _root = RandomNumberGenerator.GetBytes(32);
                WriteKeyFile(path, _root);
                _logger.LogWarning("Created a new node root key at {Path}. Back this file up together with the hash stamp backups.", path);
            }
            else
            {
                throw new InvalidOperationException(
                    $"The node root key file is missing ({path}) and existing keys depend on it. " +
                    "Restore node.key from your backup into that location and restart. Do not create a new one.");
            }

            _wrapKey = HMACSHA256.HashData(_root, Encoding.UTF8.GetBytes(WrapLabel));
        }
    }

    public byte[] DeriveStorageKey(string purpose)
    {
        return HMACSHA256.HashData(Root, Encoding.UTF8.GetBytes("processor-node/storage/v1|" + purpose));
    }

    public string DerivePeriodKey(DateTime stampTime)
    {
        string input = DerivationLabel + Database.Models.ServerHashStamp.FormatStampTime(stampTime);
        byte[] key = HMACSHA256.HashData(Root, Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(key).ToLowerInvariant();
    }

    public string Wrap(string plaintext)
    {
        _ = Root;
        byte[] data = Encoding.UTF8.GetBytes(plaintext);
        byte[] nonce = RandomNumberGenerator.GetBytes(12);
        byte[] cipher = new byte[data.Length];
        byte[] tag = new byte[16];
        using (var gcm = new AesGcm(_wrapKey!))
        {
            gcm.Encrypt(nonce, data, cipher, tag);
        }
        return WrapPrefix + Convert.ToBase64String(nonce.Concat(cipher).Concat(tag).ToArray());
    }

    public string Unwrap(string wrapped)
    {
        _ = Root;
        if (!wrapped.StartsWith(WrapPrefix)) throw new CryptographicException("Unknown wrapped key format.");
        byte[] all = Convert.FromBase64String(wrapped[WrapPrefix.Length..]);
        if (all.Length < 12 + 16) throw new CryptographicException("Wrapped key is too short.");
        byte[] nonce = all[..12];
        byte[] tag = all[^16..];
        byte[] cipher = all[12..^16];
        byte[] plain = new byte[cipher.Length];
        using (var gcm = new AesGcm(_wrapKey!))
        {
            gcm.Decrypt(nonce, cipher, tag, plain); // throws if the root key is wrong or the value was altered
        }
        return Encoding.UTF8.GetString(plain);
    }

    public void CopyKeyFileTo(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            string target = Path.Combine(directory, KeyFileName);
            string source = KeyFilePath;
            if (File.Exists(target) && File.ReadAllBytes(target).AsSpan().SequenceEqual(File.ReadAllBytes(source))) return;
            File.Copy(source, target, overwrite: true);
            _logger.LogInformation("Node root key copied to backup folder {Dir}", directory);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not copy the node root key to {Dir}", directory);
        }
    }

    private bool TryRestoreFromBackup(string path)
    {
        var s = _settings.CurrentValue;
        var candidates = new List<string>();
        if (s.HashStampBackupEnabled && !string.IsNullOrWhiteSpace(s.HashStampBackupFilePath))
            candidates.Add(Path.Combine(s.HashStampBackupFilePath, KeyFileName));

        // Where builds before the node-home fix kept it: the shared program folder. Every node started from that
        // program folder used that one key, so it is the right key for any of their homes. Copied, not moved.
        string configured = s.NodeKeyFilePath;
        string relative = string.IsNullOrWhiteSpace(configured) ? DefaultRelativePath : configured;
        if (NodeHome.IsSeparate && !Path.IsPathRooted(relative))
            candidates.Add(Path.GetFullPath(Path.Combine(NodeHome.AppFolder, relative)));

        foreach (var candidate in candidates)
        {
            try
            {
                if (!File.Exists(candidate)) continue;
                ReadKeyFile(candidate); // validates
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.Copy(candidate, path);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Backup copy of the node root key at {Path} is unusable", candidate);
            }
        }
        return false;
    }

    private static byte[] ReadKeyFile(string path)
    {
        var file = JsonSerializer.Deserialize<KeyFile>(File.ReadAllText(path))
                   ?? throw new InvalidOperationException($"Node root key file {path} is empty.");
        byte[] key = Convert.FromBase64String(file.Key);
        if (key.Length != 32) throw new InvalidOperationException($"Node root key file {path} is invalid.");
        string check = Convert.ToHexString(SHA256.HashData(key)).ToLowerInvariant();
        if (!string.Equals(check, file.Check, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Node root key file {path} is damaged (checksum mismatch).");
        return key;
    }

    private static void WriteKeyFile(string path, byte[] key)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var file = new KeyFile
        {
            Created = DateTime.UtcNow,
            Key = Convert.ToBase64String(key),
            Check = Convert.ToHexString(SHA256.HashData(key)).ToLowerInvariant()
        };
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(file, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, path, overwrite: true);
        // Note: on Linux restrict the folder to the service account (chmod 700); .NET 6 has no portable API for file modes.
    }
}
