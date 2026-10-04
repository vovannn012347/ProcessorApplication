using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Org.BouncyCastle.Asn1.Ocsp;

using P2PModule.Configuration;
using P2PModule.Infrastructure;
using P2PModule.Interfaces;
using P2PModule.Models;
using P2PModule.Models.Network;
using P2PModule.Services.Maintenance;

using ProcessorApplication.Services.HashStamps;

namespace P2PModule.Services.Persistence;

public class NodeConfig
{
    public string NodeId { get; set; } = string.Empty;
    public byte[] PublicKey { get; set; } = Array.Empty<byte>();
    public byte[] PrivateKey { get; set; } = Array.Empty<byte>();
    public byte[] PrivateSignatureKey { get; set; } = Array.Empty<byte>();
    public byte[] PublicVerifykey { get; set; } = Array.Empty<byte>();
    public string LastMasterKey { get; set; } = string.Empty; //last hash
    public DateTime SaveTime { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Manages the immutable local Node ID and deterministically rotating daily public/private keys.
/// Uses the daily cryptographic hash chain stamps from IHashStampService to compute active signing coordinates,
/// ensuring the long-term Node ID remains untouched across rotations to preserve reputation.
/// </summary>
public class NodeIdentityService : INodeIdentityService, IDisposable
{
    public string GetLocalNodeId()
    {
        lock (_lock) { return _nodeId; }
    }
    public string NodeId => _nodeId;
    public byte[] PublicKey { get { lock (_lock) { return _dailyPublicKey ; } } }
    
    public byte[] PrivateKey { get { lock (_lock) { return _dailyPrivateKey; } } }
    public byte[] PublicVerifyKey { get { lock (_lock) { return _dailyVerifyKey; } } }
    public byte[] PrivateSignatureKey { get { lock (_lock) { return _dailySignatureKey; } } }
    //public byte[] RefreshVerify { get { lock (_lock) { return _refreshVerifyKey; } } }
    //public byte[] RefreshCert { get { lock (_lock) { return _refreshCert; } } }

    public List<string> GetEndpoints() => _p2pSettings.CurrentValue.Endpoints;
    public List<string> GetCapablities() => _p2pSettings.CurrentValue.NodeRoles.ToList();
    public bool HasRole(string role) => _p2pSettings.CurrentValue.NodeRoles.Contains(role, StringComparer.OrdinalIgnoreCase);

    private string _nodeId = string.Empty;
    private byte[] _dailyPrivateKey = Array.Empty<byte>();
    private byte[] _dailyPublicKey = Array.Empty<byte>();
    private byte[] _dailySignatureKey = Array.Empty<byte>();
    private byte[] _dailyVerifyKey = Array.Empty<byte>();
    //private byte[] _refreshCert = Array.Empty<byte>();
    //private byte[] _refreshVerifyKey = Array.Empty<byte>();
    private string _masterKey = string.Empty;
    private DateTime _lastSaveTime;

    private string _identityPath = string.Empty;
    private string _identityHistoryPath = string.Empty;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptionsMonitor<P2PSettings> _p2pSettings;
    private readonly ILogger<NodeIdentityService> _logger;
    private readonly IConsensusChannelRegistry _channelRegistry;

    private readonly object _lock = new();
    private readonly IDisposable _settingsChangeListener;
    private static readonly Regex DateFolderRegex = new(@"^\d{4}-\d{2}-\d{2}$", RegexOptions.Compiled);

    public event Action<string>? OnIdentityUpdated;

    public NodeIdentityService(
        ILogger<NodeIdentityService> logger,
        IOptionsMonitor<P2PSettings> settings,
        IServiceScopeFactory scopeFactory,
        IConsensusChannelRegistry channelRegistry)
    {
        //_hashStampService = hashStampService; 
        _scopeFactory = scopeFactory;
        _p2pSettings = settings;
        _logger = logger;
        _channelRegistry = channelRegistry;

        UpdateSettings(_p2pSettings.CurrentValue);
        _settingsChangeListener = _p2pSettings.OnChange((settings, name) =>
        {
            UpdateSettings(settings);
        });
        //LoadOrCreateIdentity();
    }

    public void TriggerUpdate()
    {
        InvokeOnIdentityUpdated(_lastSaveTime.ToString("yyyy-MM-dd"));
    }

    private void InvokeOnIdentityUpdated(string time)
    {
        var handlers = OnIdentityUpdated?.GetInvocationList();

        if (handlers != null)
        {
            foreach (Action<string> handler in handlers)
            {
                _ = Task.Run(() =>
                {
                    try
                    {
                        handler(time);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Handshake audit handler failed.");
                    }
                });
            }
        }
    }

    private void UpdateSettings(P2PSettings settings)
    {
        string basePath = settings.DataStoragePath ?? "./Module_Data/P2P";
        _identityPath = Path.Combine(basePath, "identity", "node_identity.json");//current identity
        _identityHistoryPath = Path.Combine(basePath, "identity", "history");
    }

    /// <summary>
    /// Loads the persistent configuration from disk.
    /// Spawns a brand new permanent Node ID and master seed ONLY if the config is unreadable or missing.
    /// </summary>
    public void LoadOrCreateIdentity()
    {
        lock (_lock)
        {
            try
            {
                if (File.Exists(_identityPath))
                {
                    string json = File.ReadAllText(_identityPath);
                    var config = JsonSerializer.Deserialize<NodeConfig>(json);
                    if (config != null && !string.IsNullOrEmpty(config.NodeId) && config.PrivateKey?.Length > 0)
                    {
                        _nodeId = config.NodeId;
                        _dailyPrivateKey = config.PrivateKey;
                        _dailyPublicKey = config.PublicKey;
                        _dailySignatureKey = config.PrivateSignatureKey;
                        _dailyVerifyKey = config.PublicVerifykey;

                        _masterKey = config.LastMasterKey ?? string.Empty;
                        _lastSaveTime = config.SaveTime;

                        _logger.LogInformation("[Identity] Loaded permanent Node ID: {NodeId}", _nodeId);

                        UpdateDailyKeys(forceUpdate: true);
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Identity] Failed to read persistent identity config file. Regenerating...");
            }

            GenerateAndSaveNewIdentity();
        }
    }

    /// <summary>
    /// Generates a completely new unique Node ID and 32-byte master seed, writing it persistently to disk.
    /// Then triggers daily key updates.
    /// </summary>
    private void GenerateAndSaveNewIdentity()
    {
        string? directoryPath = Path.GetDirectoryName(_identityPath);
        if (!string.IsNullOrEmpty(directoryPath) && !Directory.Exists(directoryPath))
        {
            Directory.CreateDirectory(directoryPath);
        }
        if (!string.IsNullOrEmpty(_identityHistoryPath) && !Directory.Exists(_identityHistoryPath))
        {
            Directory.CreateDirectory(_identityHistoryPath);
        }

        var nowUtc = DateTime.UtcNow;

        try
        {
            if (string.IsNullOrEmpty(_p2pSettings.CurrentValue.EnforcedNodeIdentifier))
            {
                _nodeId = "node-" + Guid.NewGuid().ToString("n")[..16];
            }
            else
            {
                _nodeId = _p2pSettings.CurrentValue.EnforcedNodeIdentifier;
            }

            // Fetch today's master key from the HashStampService
            UpdateDailyKeys(forceUpdate: true);
            SaveCurrentConfig();

            _logger.LogInformation("[Identity] Successfully generated and stored fresh Node ID: {NodeId}", _nodeId);
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "[Identity] Critical failure generating or saving local node identity.");
            throw new CryptographicException("Failed to initialize NodeIdentityService workspace files.", ex);
        }
    }

    /// <summary>
    /// Derives daily rotating EC public/private keys using HMAC-SHA256 from the master seed and the cryptographic hash chain.
    /// The NodeId is NEVER modified or reset during this call.
    /// </summary>
    public void UpdateDailyKeys(bool forceUpdate = false)
    {
        string? directoryPath = Path.GetDirectoryName(_identityPath);
        if (!string.IsNullOrEmpty(directoryPath) && !Directory.Exists(directoryPath))
        {
            Directory.CreateDirectory(directoryPath);
        }
        if (!string.IsNullOrEmpty(_identityHistoryPath) && !Directory.Exists(_identityHistoryPath))
        {
            Directory.CreateDirectory(_identityHistoryPath);
        }

        var nowUtc = DateTime.UtcNow;

        try
        {
            string masterKey;

            using (var scope = _scopeFactory.CreateScope())
            {
                var hashStampService = scope.ServiceProvider.GetRequiredService<IHashStampService>();

                // Fetch today's master key from the HashStampService within the scope boundary
                var stamp = hashStampService.GetHashByTimeAsync(nowUtc).GetAwaiter().GetResult();
                if (stamp == null)
                {
                    stamp = hashStampService.GetLatestHashAsync().GetAwaiter().GetResult();
                }

                masterKey = stamp?.MasterKey ?? "fallback-" + nowUtc.ToString("yyyy-MM-dd");
            }
            var prevSaveTime = _lastSaveTime;

            if (masterKey == _masterKey &&
                _dailyPrivateKey.Length > 0 &&
                _dailyPublicKey.Length > 0 &&
                _dailySignatureKey.Length > 0 &&
                _dailyVerifyKey.Length > 0 &&
                !forceUpdate)
                return;

            lock (_lock)
            {
                var seedTime = DateTime.UtcNow;
                GenerateEncryptionKeys(masterKey, seedTime);
                GenerateSignatureKeys(masterKey, seedTime);

                //if(prevSaveTime.Date < seedTime.Date || _refreshCert.Length == 0 || _refreshVerifyKey.Length == 0)
                //{
                //    GenerateRefreshKeys(masterKey, seedTime);
                //}

                // Derive the daily private key from our master seed, hash chain's master key, and nodeId
                _masterKey = masterKey;
                _lastSaveTime = seedTime;

                _logger.LogInformation("[Identity] Deterministically derived daily rotating EC key block from hash stamp/master key.");

                SaveCurrentConfig();
            }
            InvokeOnIdentityUpdated(prevSaveTime.ToString("yyyy-MM-dd"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Identity] Failed to execute scoped hash derivation pipeline. Falling back to static values.");
            FallbackToStaticKeysInternal(forceUpdate);
        }
    }

    private void GenerateEncryptionKeys(string masterKey, DateTime seedTime)
    {
        byte[] seed = P2PCrypto.DeriveSeed(masterKey, "Encryption_v1" + seedTime.ToString());

        P2PCrypto.DeriveKeyPairFromSeed(seed, out _dailyPrivateKey, out _dailyPublicKey);
    }

    private void GenerateSignatureKeys(string masterKey, DateTime seedTime)
    {
        byte[] seed = P2PCrypto.DeriveSeed(masterKey, "Signature_v1" + seedTime.ToString());

        P2PCrypto.DeriveSignaturePairFromSeed(seed, out _dailySignatureKey, out _dailyVerifyKey);
    }

    //private void GenerateRefreshKeys(string masterKey, DateTime seedTime)
    //{
    //    byte[] seed = P2PCrypto.DeriveSeed(masterKey, "Refresh_v1" + seedTime.ToString());

    //    P2PCrypto.DeriveSignaturePairFromSeed(seed, out _refreshCert, out _refreshVerifyKey);
    //}

    /// <summary>
    /// Robust daily fallback mechanism using UTC dates if the cryptographic HashStamp service is unreachable.
    /// </summary>
    private void FallbackToStaticKeysInternal(bool forceCheck)
    {
        lock (_lock)
        {
            if (_dailyPrivateKey.Length > 0 && _dailyPublicKey.Length > 0 && _masterKey == "fallback" && !forceCheck)
                return;

            string todayStr = DateTime.UtcNow.ToString("yyyy-MM-dd");
            string masterKey = todayStr + "|" + _nodeId + "|fallback-salting-chain-v1";

            var prevSaveTime = _lastSaveTime;

            try
            {
                var seedTime = DateTime.UtcNow;
                GenerateEncryptionKeys(masterKey, seedTime);
                GenerateSignatureKeys(masterKey, seedTime);
                //if (prevSaveTime.Date < seedTime.Date)
                //{
                //    GenerateRefreshKeys(masterKey, seedTime);
                //}
                _masterKey = masterKey;
                _lastSaveTime = seedTime;

                SaveCurrentConfig();
            }
            catch (Exception ex)
            {
                _logger.LogCritical(ex, "[Identity] Fallback daily key derivation failed.");
                throw new CryptographicException("Core cryptographic daily derivation failed.", ex);
            }
            InvokeOnIdentityUpdated(prevSaveTime.ToString("yyyy-MM-dd"));
        }
    }

    /// <summary>
    /// Writes the loaded or freshly generated config directly to disk.
    /// </summary>
    private void SaveCurrentConfig()
    {
        try
        {
            var config = new NodeConfig
            {
                NodeId = _nodeId,
                PrivateKey = _dailyPrivateKey,
                PublicKey = _dailyPublicKey,
                PrivateSignatureKey = _dailySignatureKey,
                PublicVerifykey = _dailyVerifyKey,
                LastMasterKey = _masterKey,
                SaveTime = _lastSaveTime
            };
            string json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_identityPath, json);

            //history
            string todayStr = DateTime.UtcNow.ToString("yyyy-MM-dd");
            var histPath = Path.Combine(_identityHistoryPath, todayStr);
            if (!string.IsNullOrEmpty(histPath) && !Directory.Exists(histPath))
            {
                Directory.CreateDirectory(histPath);
            }
            File.WriteAllText(Path.Combine(histPath, "PrivateEnc"), Convert.ToBase64String(_dailyPrivateKey));
            File.WriteAllText(Path.Combine(histPath, "PublicEnc"), Convert.ToBase64String(_dailyPublicKey));
            File.WriteAllText(Path.Combine(histPath, "PrivateSig"), Convert.ToBase64String(_dailySignatureKey));
            File.WriteAllText(Path.Combine(histPath, "PublicSig"), Convert.ToBase64String(_dailyVerifyKey));
            //File.WriteAllText(Path.Combine(histPath, "PrivateRef"), Convert.ToBase64String(_refreshCert));
            //File.WriteAllText(Path.Combine(histPath, "PublicRef"), Convert.ToBase64String(_refreshVerifyKey));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Identity] Failed to persist updated configuration to disk.");
        }
    }

    /// <summary>
    /// Purges the existing local storage file and resets all memory caches of derived daily keys.
    /// </summary>
    public void ResetIdentity()
    {
        lock (_lock)
        {
            _logger.LogWarning("[Identity] Resetting local node identity. Master keys will be purged.");
            try
            {
                if (File.Exists(_identityPath))
                {
                    File.Delete(_identityPath);
                }
                if (Directory.Exists(_identityHistoryPath))
                {
                    Directory.Delete(_identityHistoryPath, true);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Identity] Failed to clear identity file.");
            }

            _nodeId = string.Empty;
            _dailyPrivateKey = Array.Empty<byte>();
            _dailyPublicKey = Array.Empty<byte>();
            _dailySignatureKey = Array.Empty<byte>();
            _dailyVerifyKey = Array.Empty<byte>();
            _masterKey = string.Empty;

            GenerateAndSaveNewIdentity();
        }
    }

    private byte[]? ReadHistoryKey(string date, string keyName)
    {
        string filePath = Path.Combine(_identityHistoryPath, date, keyName);

        if (!File.Exists(filePath))
        {
            return null;
        }

        try
        {
            string base64 = File.ReadAllText(filePath);
            return Convert.FromBase64String(base64);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Identity] Failed to read or decode history key {Name} for date {Date}", keyName, date);
            return null;
        }
    }

    public byte[]? GetPublicKey(string day = "")
    {
        if (!string.IsNullOrEmpty(day))
        {
            return ReadHistoryKey(day, "PublicEnc");
        }

        return _dailyPublicKey;
    }

    public byte[]? GetPrivateKey(string day = "")
    {
        if (!string.IsNullOrEmpty(day))
        {
            return ReadHistoryKey(day, "PrivateEnc");
        }

        return _dailyPrivateKey;
    }

    public byte[]? GetPublicVerificator(string day = "")
    {
        if (!string.IsNullOrEmpty(day))
        {
            return ReadHistoryKey(day, "PublicSig");
        }

        return _dailyVerifyKey;
    }

    public byte[]? GetPrivateCertificate(string day = "")
    {
        if (!string.IsNullOrEmpty(day))
        {
            return ReadHistoryKey(day, "PrivateSig");
        }

        return _dailySignatureKey;
    }


    //public byte[]? GetPublicRefresh(string day = "")
    //{
    //    if (!string.IsNullOrEmpty(day))
    //    {
    //        return ReadHistoryKey(day, "PublicRef");
    //    }

    //    return _refreshVerifyKey;
    //}

    //public byte[]? GetPrivateRefresh(string day = "")
    //{
    //    if (!string.IsNullOrEmpty(day))
    //    {
    //        return ReadHistoryKey(day, "PrivateRef");
    //    }

    //    return _refreshCert;
    //}



    public List<string> GetRecentDayKeys(int n)
    {
        if (string.IsNullOrEmpty(_identityHistoryPath) || !Directory.Exists(_identityHistoryPath))
        {
            return new List<string>();
        }

        try
        {
            return Directory.GetDirectories(_identityHistoryPath)
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrEmpty(name) &&
                               name.Length == 10 &&
                               name[4] == '-' &&
                               name[7] == '-' &&
                               DateFolderRegex.IsMatch(name))
                .OrderByDescending(name => name, StringComparer.Ordinal)
                .Take(n)
                .ToList()!;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Identity] Failed to query recent chronological day keys from path: {Path}", _identityHistoryPath);
            return new List<string>();
        }
    }

    public void Dispose()
    {
        _settingsChangeListener?.Dispose();
    }

    public PeerImmutableData GetImmutableData()
    {
        var ret =  new PeerImmutableData
        {
            NodeId = NodeId,
            PublicVerifyKey = PublicVerifyKey,
            PublicEncryptionKey = PublicKey,
            SignalingEndpoints = GetEndpoints(),
            Roles = GetCapablities(),
            ConsensusChannels = _channelRegistry.AdvertisedChannelIds.ToList(),
            SignatureTime = DateTime.UtcNow
        };

        ret.Signature = P2PCrypto.SignData(ret.GetNodeDataHash(), PrivateSignatureKey);

        return ret;
    }
}