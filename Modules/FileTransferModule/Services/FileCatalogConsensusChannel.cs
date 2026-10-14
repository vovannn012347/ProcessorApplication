using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using Common.Interfaces.Consensus;

using FileTransferModule.Configuration;
using FileTransferModule.Models;
using FileTransferModule.Models.Torrent;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using P2PModule.Interfaces;
using P2PModule.Services.Persistence;

namespace FileTransferModule.Services;

/// <summary>
/// Extended surface over IConsensusChannel specific to the file catalog, for callers
/// (FileCatalogService, controllers) that need more than the generic per-record contract.
/// </summary>
public interface IFileCatalogConsensusChannel : IConsensusChannel
{
    /// <summary>
    /// Every catalog entry currently known: own locally-hosted files (real manifest, since that
    /// costs nothing extra locally) plus everything learned from other nodes (always a shell
    /// manifest - no block hashes ever travel through the catalog, see class remarks). Entries
    /// whose Description/CustomFields were explicitly fetched carry them; others don't.
    /// Deterministically ordered (newest first) so pagination is stable. No network calls.
    /// </summary>
    Task<List<FileCatalogEntry>> GetAllKnownEntriesAsync();

    /// <summary>
    /// Saves a lightweight record learned from a direct catalogue probe (as opposed to consensus
    /// relay) - same per-host cap and newer-wins rule as ApplyRecordAsync. Never touches
    /// Description/CustomFields; SaveFullInfoAsync is the only way those get set.
    /// </summary>
    Task<bool> SaveDirectlyObservedRecordAsync(string hostNodeId, string fileIdentifier, string displayName, DateTime updatedUtc);

    /// <summary>
    /// Explicit-request path: saves a description/custom fields an admin fetched via
    /// CatalogGetFileInfo for one file. Upgrades an existing record in place, or creates one
    /// (subject to the per-host cap) if we didn't already know about this file. Returns false if
    /// it wasn't saved (cap reached, invalid ids, or claims to be our own file).
    /// </summary>
    Task<bool> SaveFullInfoAsync(string hostNodeId, string fileIdentifier, string displayName, string? description, Dictionary<string, string>? customFields);

    /// <summary>The stored record for a host/file pair, or null. Relay records only - our own files live in the archive.</summary>
    Task<FileCatalogRecord?> GetRecordAsync(string hostNodeId, string fileIdentifier);
}

/// <summary>
/// The file catalog as an epidemic-synced consensus channel.
///
/// STORAGE: one small JSON file per (host, file) at {CatalogPath}/{HostNodeId}/{FileIdentifier}.json
/// (CatalogPath read live from FileSharingSettings on every disk operation, not cached - a settings
/// change takes effect immediately for records saved/loaded from then on; records already on disk
/// under an old path are simply left where they are, no migration). A node's catalogue is a
/// directory of tiny files, never one growing document - adding, refreshing or evicting a record
/// touches only that record's file. Memory mirrors the disk layout (host -> file -> record), which
/// also makes the per-host count O(1).
///
/// WHAT NEVER LIVES HERE: block hashes. A .ptorrent manifest fully covers a file's parts (byte
/// ranges and their hashes) on its own - that's what makes arbitrary block lengths possible - and
/// it only ever travels through the dedicated download path (FetchManifestFromNodeAsync /
/// TrackerGetManifest) when someone actually wants to download the file. Neither consensus nor
/// catalog browsing carries or stores it.
///
/// TWO TIERS OF INFORMATION:
///  - Lightweight record (identifier, readable name, update time) - all consensus ever carries,
///    and all a plain catalogue browse returns.
///  - "Full info": a user-provided free-text description and flat custom fields, nothing more.
///    Saved only via an explicit per-file request (CatalogGetFileInfo) - never automatically, and
///    never as a side effect of browsing a catalogue.
///
/// LIMITS: at most MaxCatalogRecordsPerHost NEW records are accepted per host. Existing records are
/// never displaced by this - only refreshed. The cap is applied from record ids alone
/// (SelectRecordsToFetchAsync) so no payload round-trip is wasted on records that would be refused.
///
/// PRESENCE / EVICTION: a peer reporting a record we already hold refreshes its LastSeenUtc
/// (MarkRecordsStillPresentAsync). Records nobody has confirmed within CatalogRecordStaleAfterHours
/// are evicted (file deleted, empty host directory removed), which frees room under the cap for
/// genuinely new records. Our own hosted files are never stored here - they're computed live from
/// IFileArchiveService, so they never age or evict.
///
/// SECURITY: host ids and file identifiers arrive from remote peers and become path components.
/// Both are strictly validated (IsSafePathComponent) and every path is verified to stay inside the
/// catalogue root before it's touched.
/// </summary>
public class FileCatalogConsensusChannel : IFileCatalogConsensusChannel, IHostedService
{
    public const string CHANNEL_ID = "filetr.catalog";

    public string ChannelId => CHANNEL_ID;
    public string DisplayName => "Shared File Catalog";
    public int SchemaVersion => 3; // v3: full info is description/custom fields, not a manifest

    private const int MaxDisplayNameLength = 260;
    private const int MaxDescriptionLength = 4096;
    private const int MaxCustomFieldValueLength = 1024;
    private const int MaxCustomFieldCount = 32;
    private static readonly Regex SafePathComponent = new(@"^[A-Za-z0-9._-]{1,128}$", RegexOptions.Compiled);

    private readonly IFileArchiveService _archiveService;
    private readonly INodeIdentityService _identity;
    private readonly IOptionsMonitor<FileSharingSettings> _settings;
    private readonly ILogger<FileCatalogConsensusChannel> _logger;

    // host -> (fileIdentifier -> record). Mirrors {CatalogPath}/{HostNodeId}/{FileIdentifier}.json
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, FileCatalogRecord>> _byHost
        = new(StringComparer.OrdinalIgnoreCase);

    // record ids whose in-memory state is newer than their file (LastSeen refreshes)
    private readonly ConcurrentDictionary<string, byte> _dirty = new(StringComparer.OrdinalIgnoreCase);

    private CancellationTokenSource? _periodicCts;
    private Task? _periodicTask;

    public FileCatalogConsensusChannel(
        IFileArchiveService archiveService,
        INodeIdentityService identity,
        IOptionsMonitor<FileSharingSettings> settings,
        ILogger<FileCatalogConsensusChannel> logger)
    {
        _archiveService = archiveService;
        _identity = identity;
        _settings = settings;
        _logger = logger;

        LoadFromDisk();
    }

    public static string MakeRecordId(string fileIdentifier, string hostNodeId) => $"{fileIdentifier}|{hostNodeId}";

    private static bool TryParseRecordId(string recordId, out string fileIdentifier, out string hostNodeId)
    {
        fileIdentifier = string.Empty;
        hostNodeId = string.Empty;

        var parts = recordId?.Split('|', 2);
        if (parts == null || parts.Length != 2) return false;
        if (!IsSafePathComponent(parts[0]) || !IsSafePathComponent(parts[1])) return false;

        fileIdentifier = parts[0];
        hostNodeId = parts[1];
        return true;
    }

    /// <summary>
    /// Ids come from remote peers and become directory/file names. Only a conservative character
    /// set is accepted, and "." / ".." are refused outright.
    /// </summary>
    public static bool IsSafePathComponent(string? value)
        => !string.IsNullOrEmpty(value) && value != "." && value != ".." && SafePathComponent.IsMatch(value);

    private bool IsOwnNode(string hostNodeId)
        => hostNodeId.Equals(_identity.NodeId, StringComparison.OrdinalIgnoreCase);

    /// <summary>Read live on every disk operation rather than cached, so a settings change takes effect immediately.</summary>
    private string GetCatalogRoot() => Path.GetFullPath(_settings.CurrentValue.CatalogPath);

    // ------------------------------------------------------------------------------------------
    // Lifecycle: periodic flush of batched refreshes + stale-record sweep
    // ------------------------------------------------------------------------------------------

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _periodicCts = new CancellationTokenSource();
        _periodicTask = Task.Run(() => RunPeriodicMaintenanceLoopAsync(_periodicCts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _periodicCts?.Cancel();
        if (_periodicTask != null)
        {
            try { await _periodicTask; } catch { }
        }

        FlushDirtyRecords(); // don't lose refreshes made since the last cycle
    }

    private async Task RunPeriodicMaintenanceLoopAsync(CancellationToken ct)
    {
        try
        {
            var interval = TimeSpan.FromSeconds(Math.Max(5, _settings.CurrentValue.CatalogPersistDebounceSeconds));
            using var timer = new PeriodicTimer(interval);

            while (await timer.WaitForNextTickAsync(ct))
            {
                int evicted = SweepStaleRecords();
                if (evicted > 0)
                {
                    _logger.LogInformation("[FileCatalog] Evicted {Count} stale catalog record(s) (no presence confirmation in {Hours}h).",
                        evicted, _settings.CurrentValue.CatalogRecordStaleAfterHours);
                }

                FlushDirtyRecords();
            }
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
    }

    /// <summary>Removes records whose LastSeenUtc fell behind the threshold: deletes the file and any emptied host directory.</summary>
    private int SweepStaleRecords()
    {
        var threshold = DateTime.UtcNow - TimeSpan.FromHours(Math.Max(1, _settings.CurrentValue.CatalogRecordStaleAfterHours));
        int removed = 0;

        foreach (var host in _byHost)
        {
            foreach (var kv in host.Value)
            {
                if (kv.Value.LastSeenUtc >= threshold) continue;
                if (!host.Value.TryRemove(kv.Key, out _)) continue;

                _dirty.TryRemove(MakeRecordId(kv.Key, host.Key), out _);
                DeleteRecordFile(host.Key, kv.Key);
                removed++;
            }

            if (host.Value.IsEmpty)
            {
                _byHost.TryRemove(host.Key, out _);
                TryDeleteEmptyHostDirectory(host.Key);
            }
        }

        return removed;
    }

    private void FlushDirtyRecords()
    {
        foreach (var recordId in _dirty.Keys.ToList())
        {
            if (!_dirty.TryRemove(recordId, out _)) continue;
            if (!TryParseRecordId(recordId, out var fileId, out var hostId)) continue;
            if (!TryGetRecord(hostId, fileId, out var record)) continue;

            WriteRecordFile(record);
        }
    }

    // ------------------------------------------------------------------------------------------
    // IConsensusChannel - lightweight records only, never Description/CustomFields
    // ------------------------------------------------------------------------------------------

    public async Task<IReadOnlyCollection<string>> GetLocalRecordIdsAsync(CancellationToken ct = default)
    {
        var ids = new List<string>();

        foreach (var f in await _archiveService.GetTrackedFilesAsync(includeHidden: false))
        {
            if (!string.IsNullOrEmpty(f.Manifest?.ContentHash))
            {
                ids.Add(MakeRecordId(f.Manifest.ContentHash, _identity.NodeId));
            }
        }

        foreach (var host in _byHost)
        {
            if (IsOwnNode(host.Key)) continue;
            foreach (var fileId in host.Value.Keys)
            {
                ids.Add(MakeRecordId(fileId, host.Key));
            }
        }

        return ids;
    }

    public async Task<byte[]?> GetRecordPayloadAsync(string recordId, CancellationToken ct = default)
    {
        if (!TryParseRecordId(recordId, out var fileId, out var hostId)) return null;

        FileCatalogRecord wire;

        if (IsOwnNode(hostId))
        {
            var local = await _archiveService.GetTrackedFileAsync(fileId);
            // A hidden file must not be confirmable by id, even though it never appears in our id list.
            if (local == null || local.IsHidden) return null;

            wire = new FileCatalogRecord
            {
                FileIdentifier = fileId,
                HostNodeId = _identity.NodeId,
                DisplayName = local.Manifest.FileName,
                UpdatedUtc = local.RegisteredUtc
            };
        }
        else
        {
            if (!TryGetRecord(hostId, fileId, out var stored)) return null;

            // Lightweight only - Description/CustomFields never relay over consensus.
            wire = new FileCatalogRecord
            {
                FileIdentifier = stored.FileIdentifier,
                HostNodeId = stored.HostNodeId,
                DisplayName = stored.DisplayName,
                UpdatedUtc = stored.UpdatedUtc
            };
        }

        return JsonSerializer.SerializeToUtf8Bytes(wire);
    }

    public Task<IReadOnlyList<string>> SelectRecordsToFetchAsync(IReadOnlyList<string> candidateRecordIds, CancellationToken ct = default)
    {
        int cap = Math.Max(1, _settings.CurrentValue.MaxCatalogRecordsPerHost);
        var admittedPerHost = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var selected = new List<string>();

        foreach (var recordId in candidateRecordIds)
        {
            if (!TryParseRecordId(recordId, out _, out var hostId)) continue;
            if (IsOwnNode(hostId)) continue;

            admittedPerHost.TryGetValue(hostId, out int alreadyAdmitted);
            if (CountForHost(hostId) + alreadyAdmitted >= cap) continue; // this host is full - don't even ask for it

            admittedPerHost[hostId] = alreadyAdmitted + 1;
            selected.Add(recordId);
        }

        return Task.FromResult<IReadOnlyList<string>>(selected);
    }

    public Task<ConsensusMergeResolution> ResolveConflictAsync(string recordId, byte[] incomingPayload, byte[]? existingPayload, CancellationToken ct = default)
    {
        if (!TryParseRecordId(recordId, out var fileId, out var hostId))
            return Task.FromResult(ConsensusMergeResolution.Reject);

        // We are the sole authority for our own hosted files.
        if (IsOwnNode(hostId))
            return Task.FromResult(ConsensusMergeResolution.Reject);

        if (!TryReadWireRecord(incomingPayload, fileId, hostId, out var incoming))
            return Task.FromResult(ConsensusMergeResolution.Reject);

        if (!TryGetRecord(hostId, fileId, out var existing))
        {
            // Brand-new record: admit only while this host is under its cap. Existing records are
            // never displaced by this - it only bounds how many NEW ones we take on.
            return Task.FromResult(CountForHost(hostId) >= Math.Max(1, _settings.CurrentValue.MaxCatalogRecordsPerHost)
                ? ConsensusMergeResolution.Reject
                : ConsensusMergeResolution.AcceptIncoming);
        }

        return Task.FromResult(ClampToNow(incoming.UpdatedUtc) <= existing.UpdatedUtc
            ? ConsensusMergeResolution.KeepExisting
            : ConsensusMergeResolution.AcceptIncoming);
    }

    public Task ApplyRecordAsync(string recordId, byte[] payload, CancellationToken ct = default)
    {
        if (!TryParseRecordId(recordId, out var fileId, out var hostId)) return Task.CompletedTask;
        if (IsOwnNode(hostId)) return Task.CompletedTask; // never let a relayed record claim to be ours
        if (!TryReadWireRecord(payload, fileId, hostId, out var incoming)) return Task.CompletedTask;

        bool isNew = !TryGetRecord(hostId, fileId, out var record);
        if (isNew && CountForHost(hostId) >= Math.Max(1, _settings.CurrentValue.MaxCatalogRecordsPerHost))
            return Task.CompletedTask; // defensive: Resolve should already have refused this

        if (isNew)
        {
            record = new FileCatalogRecord { FileIdentifier = fileId, HostNodeId = hostId };
        }

        // Lightweight fields only. Description/CustomFields/FullInfoFetchedUtc are deliberately
        // left as-is on a refresh - a consensus/browse update must never discard full info an
        // admin explicitly fetched earlier.
        record!.DisplayName = incoming.DisplayName; // already sanitized by TryReadWireRecord
        record.UpdatedUtc = ClampToNow(incoming.UpdatedUtc);
        record.LastSeenUtc = DateTime.UtcNow; // being accepted now is itself a presence confirmation

        GetOrCreateBucket(hostId)[fileId] = record;
        WriteRecordFile(record); // new/changed records are written right away; only "still present" touches are batched
        return Task.CompletedTask;
    }

    public Task MarkRecordsStillPresentAsync(IEnumerable<string> recordIds, CancellationToken ct = default)
    {
        if (recordIds == null) return Task.CompletedTask;

        var now = DateTime.UtcNow;
        var minRefresh = TimeSpan.FromMinutes(30); // eviction works in hours; finer than this only churns files

        foreach (var recordId in recordIds)
        {
            // Only relay records age/evict - our own are computed live and never stored here.
            if (!TryParseRecordId(recordId, out var fileId, out var hostId)) continue;
            if (!TryGetRecord(hostId, fileId, out var record)) continue;
            if (now - record.LastSeenUtc < minRefresh) continue;

            record.LastSeenUtc = now;
            _dirty[recordId] = 0; // flushed on the next periodic cycle, not per call
        }

        return Task.CompletedTask;
    }

    // ------------------------------------------------------------------------------------------
    // IFileCatalogConsensusChannel
    // ------------------------------------------------------------------------------------------

    public async Task<List<FileCatalogEntry>> GetAllKnownEntriesAsync()
    {
        var result = new List<FileCatalogEntry>();

        foreach (var f in await _archiveService.GetTrackedFilesAsync(includeHidden: false))
        {
            if (string.IsNullOrEmpty(f.Manifest?.ContentHash)) continue;

            result.Add(new FileCatalogEntry
            {
                Manifest = f.Manifest, // real manifest - free to include, it's our own data
                HostNodeId = _identity.NodeId,
                HostSignalingEndpoints = _identity.GetEndpoints(),
                IsLocalSeeding = true,
                DiscoveredUtc = f.RegisteredUtc,
                Description = f.Description,
                CustomFields = f.CustomFields ?? new Dictionary<string, string>(),
                FullInfoFetchedUtc = f.RegisteredUtc // it's ours, always "known"
            });
        }

        foreach (var host in _byHost)
        {
            if (IsOwnNode(host.Key)) continue;
            foreach (var record in host.Value.Values)
            {
                result.Add(ToEntry(record));
            }
        }

        // Stable order so page N is the same slice on every call (dictionary enumeration isn't).
        return result
            .OrderByDescending(e => e.DiscoveredUtc)
            .ThenBy(e => e.Manifest.ContentHash, StringComparer.Ordinal)
            .ThenBy(e => e.HostNodeId, StringComparer.Ordinal)
            .ToList();
    }

    public Task<bool> SaveDirectlyObservedRecordAsync(string hostNodeId, string fileIdentifier, string displayName, DateTime updatedUtc)
    {
        if (!IsSafePathComponent(hostNodeId) || !IsSafePathComponent(fileIdentifier)) return Task.FromResult(false);
        if (IsOwnNode(hostNodeId)) return Task.FromResult(false);

        string sanitizedName = SanitizeDisplayName(displayName);
        if (sanitizedName.Length == 0) return Task.FromResult(false);

        bool isNew = !TryGetRecord(hostNodeId, fileIdentifier, out var record);
        var clampedUpdated = ClampToNow(updatedUtc);

        if (!isNew && clampedUpdated <= record!.UpdatedUtc)
        {
            // Not newer than what we have - still counts as a presence confirmation.
            record.LastSeenUtc = DateTime.UtcNow;
            _dirty[MakeRecordId(fileIdentifier, hostNodeId)] = 0;
            return Task.FromResult(true);
        }

        if (isNew && CountForHost(hostNodeId) >= Math.Max(1, _settings.CurrentValue.MaxCatalogRecordsPerHost))
            return Task.FromResult(false);

        if (isNew)
        {
            record = new FileCatalogRecord { FileIdentifier = fileIdentifier, HostNodeId = hostNodeId };
        }

        record!.DisplayName = sanitizedName;
        record.UpdatedUtc = clampedUpdated;
        record.LastSeenUtc = DateTime.UtcNow;

        GetOrCreateBucket(hostNodeId)[fileIdentifier] = record;
        WriteRecordFile(record);
        return Task.FromResult(true);
    }

    public Task<bool> SaveFullInfoAsync(string hostNodeId, string fileIdentifier, string displayName, string? description, Dictionary<string, string>? customFields)
    {
        if (!IsSafePathComponent(hostNodeId) || !IsSafePathComponent(fileIdentifier)) return Task.FromResult(false);
        if (IsOwnNode(hostNodeId)) return Task.FromResult(false);

        string sanitizedName = SanitizeDisplayName(displayName);
        if (sanitizedName.Length == 0) return Task.FromResult(false);

        bool isNew = !TryGetRecord(hostNodeId, fileIdentifier, out var record);
        if (isNew && CountForHost(hostNodeId) >= Math.Max(1, _settings.CurrentValue.MaxCatalogRecordsPerHost))
        {
            _logger.LogInformation("[FileCatalog] Not saving info for {File} from {Host}: per-host cap reached.", fileIdentifier, hostNodeId);
            return Task.FromResult(false);
        }

        if (isNew)
        {
            record = new FileCatalogRecord
            {
                FileIdentifier = fileIdentifier,
                HostNodeId = hostNodeId,
                UpdatedUtc = DateTime.UtcNow
            };
        }

        var (filteredDescription, filteredFields) = FilterFieldsThroughAccessList(description, customFields);
        record!.DisplayName = sanitizedName;
        record.LastSeenUtc = DateTime.UtcNow;
        record.Description = filteredDescription;
        record.CustomFields = filteredFields;
        record.FullInfoFetchedUtc = DateTime.UtcNow;

        GetOrCreateBucket(hostNodeId)[fileIdentifier] = record;
        _dirty.TryRemove(MakeRecordId(fileIdentifier, hostNodeId), out _);
        WriteRecordFile(record); // explicit request - persist immediately
        return Task.FromResult(true);
    }

    public Task<FileCatalogRecord?> GetRecordAsync(string hostNodeId, string fileIdentifier)
        => Task.FromResult<FileCatalogRecord?>(TryGetRecord(hostNodeId, fileIdentifier, out var r) ? r : null);

    // ------------------------------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------------------------------

    private ConcurrentDictionary<string, FileCatalogRecord> GetOrCreateBucket(string hostId)
        => _byHost.GetOrAdd(hostId, _ => new ConcurrentDictionary<string, FileCatalogRecord>(StringComparer.OrdinalIgnoreCase));

    private int CountForHost(string hostId)
        => _byHost.TryGetValue(hostId, out var bucket) ? bucket.Count : 0;

    private bool TryGetRecord(string hostId, string fileId, out FileCatalogRecord record)
    {
        record = null!;
        return _byHost.TryGetValue(hostId, out var bucket) && bucket.TryGetValue(fileId, out record!);
    }

    /// <summary>
    /// Deserializes and validates a lightweight wire record. The record must describe exactly the
    /// host/file its id names (so a peer can't smuggle a different identity inside a valid-looking
    /// id), and its name is sanitized.
    /// </summary>
    private bool TryReadWireRecord(byte[] payload, string expectedFileId, string expectedHostId, out FileCatalogRecord record)
    {
        record = null!;

        try
        {
            var parsed = JsonSerializer.Deserialize<FileCatalogRecord>(payload);
            if (parsed == null) return false;

            if (!string.Equals(parsed.FileIdentifier, expectedFileId, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(parsed.HostNodeId, expectedHostId, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            parsed.DisplayName = SanitizeDisplayName(parsed.DisplayName);
            if (parsed.DisplayName.Length == 0) return false;

            record = parsed;
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[FileCatalog] Malformed catalog record for {FileId} from {HostId}.", expectedFileId, expectedHostId);
            return false;
        }
    }

    private static string SanitizeDisplayName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;

        var sb = new StringBuilder(Math.Min(name.Length, MaxDisplayNameLength));
        foreach (char c in name)
        {
            if (char.IsControl(c)) continue;
            sb.Append(c);
            if (sb.Length >= MaxDisplayNameLength) break;
        }

        return sb.ToString().Trim();
    }

    /// <summary>The reserved field name used to let a rule target the free-text description specifically.</summary>
    private const string DescriptionFieldName = "description";

    /// <summary>
    /// Applies FileSharingSettings.CustomFieldAccessRules (an ordered allow/deny access list,
    /// matched by field name and optionally by value content via regex) to a description/custom
    /// fields set received from another host. "description" is the reserved field name for the
    /// free-text description, so a rule can target it the same way as any custom field.
    ///
    /// A hard structural cap (control-char stripping, max length, max field count) is applied
    /// first, before any rule sees the content - this is a safety net independent of rule
    /// configuration, not a substitute for it: a misconfigured or empty rule set can still only
    /// ever let through bounded data, never unbounded.
    /// </summary>
    private (string? description, Dictionary<string, string> customFields) FilterFieldsThroughAccessList(string? description, Dictionary<string, string>? customFields)
    {
        var rules = _settings.CurrentValue.CustomFieldAccessRules ?? new List<CustomFieldAccessRule>();
        var defaultAction = _settings.CurrentValue.CustomFieldDefaultAction;

        string? filteredDescription = null;
        string cappedDescription = CapText(description, MaxDescriptionLength);
        if (cappedDescription.Length > 0 && EvaluateFieldAccess(DescriptionFieldName, cappedDescription, rules, defaultAction) == FieldRuleAction.Allow)
        {
            filteredDescription = cappedDescription;
        }

        var filteredFields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (customFields != null)
        {
            foreach (var kv in customFields)
            {
                if (filteredFields.Count >= MaxCustomFieldCount) break;
                if (string.IsNullOrWhiteSpace(kv.Key) || kv.Key.Length > 128) continue;
                if (kv.Key.Equals(DescriptionFieldName, StringComparison.OrdinalIgnoreCase)) continue; // reserved name

                string name = kv.Key.Trim();
                string value = CapText(kv.Value, MaxCustomFieldValueLength);

                if (EvaluateFieldAccess(name, value, rules, defaultAction) == FieldRuleAction.Allow)
                {
                    filteredFields[name] = value;
                }
            }
        }

        return (filteredDescription, filteredFields);
    }

    /// <summary>
    /// Walks the rules in order; the first whose FieldNamePattern matches the name AND whose
    /// ValuePattern (if any) matches the value decides the outcome. A bad regex in a rule is
    /// logged once and treated as non-matching (skipped), rather than throwing and dropping every
    /// field behind it. A short match timeout guards against a catastrophic-backtracking pattern
    /// hanging the sync loop.
    /// </summary>
    private FieldRuleAction EvaluateFieldAccess(string name, string value, List<CustomFieldAccessRule> rules, FieldRuleAction defaultAction)
    {
        var matchTimeout = TimeSpan.FromMilliseconds(100);

        foreach (var rule in rules)
        {
            if (string.IsNullOrEmpty(rule.FieldNamePattern)) continue;

            if (!TryRegexMatch(rule.FieldNamePattern, name, matchTimeout, out bool nameMatches))
            {
                _logger.LogWarning("[FileCatalog] Skipping custom-field rule with invalid FieldNamePattern '{Pattern}'.", rule.FieldNamePattern);
                continue;
            }
            if (!nameMatches) continue;

            bool valueMatches = true;
            if (!string.IsNullOrEmpty(rule.ValuePattern))
            {
                if (!TryRegexMatch(rule.ValuePattern, value, matchTimeout, out valueMatches))
                {
                    _logger.LogWarning("[FileCatalog] Skipping custom-field rule with invalid ValuePattern '{Pattern}'.", rule.ValuePattern);
                    continue;
                }
            }

            if (valueMatches) return rule.Action; // first fully-matching rule wins
        }

        return defaultAction;
    }

    private static bool TryRegexMatch(string pattern, string input, TimeSpan timeout, out bool isMatch)
    {
        try
        {
            isMatch = Regex.IsMatch(input, pattern, RegexOptions.None, timeout);
            return true;
        }
        catch (ArgumentException)
        {
            isMatch = false;
            return false; // malformed pattern
        }
        catch (RegexMatchTimeoutException)
        {
            isMatch = false;
            return true; // valid pattern, just didn't match in time - treat as non-matching, not an error
        }
    }

    /// <summary>Strips control characters (keeping \n/\r/\t) and caps length - the hard safety net independent of access-list rules.</summary>
    private static string CapText(string? text, int maxLength)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        var sb = new StringBuilder(Math.Min(text.Length, maxLength));
        foreach (char c in text)
        {
            if (char.IsControl(c) && c != '\n' && c != '\r' && c != '\t') continue;
            sb.Append(c);
            if (sb.Length >= maxLength) break;
        }

        return sb.ToString().Trim();
    }

    /// <summary>A host can't claim a timestamp meaningfully in the future.</summary>
    private static DateTime ClampToNow(DateTime value)
    {
        var utc = value.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(value, DateTimeKind.Utc) : value.ToUniversalTime();
        var ceiling = DateTime.UtcNow.AddDays(1);
        return utc > ceiling ? DateTime.UtcNow : utc;
    }

    /// <summary>Builds the entry consumers see: a shell manifest (no block hashes) plus whatever full info has been fetched, if any.</summary>
    private static FileCatalogEntry ToEntry(FileCatalogRecord record)
    {
        return new FileCatalogEntry
        {
            Manifest = new PTorrentManifest
            {
                ContentHash = record.FileIdentifier,
                FileName = record.DisplayName
            },
            HostNodeId = record.HostNodeId,
            HostSignalingEndpoints = new List<string>(),
            IsLocalSeeding = false,
            DiscoveredUtc = record.UpdatedUtc,
            Description = record.Description,
            CustomFields = record.CustomFields,
            FullInfoFetchedUtc = record.FullInfoFetchedUtc
        };
    }

    // ------------------------------------------------------------------------------------------
    // Disk: {CatalogPath}/{HostNodeId}/{FileIdentifier}.json
    // ------------------------------------------------------------------------------------------

    private bool TryGetSafeRecordPath(string hostId, string fileId, out string path)
    {
        path = string.Empty;
        if (!IsSafePathComponent(hostId) || !IsSafePathComponent(fileId)) return false;

        string root = GetCatalogRoot();
        string rootPrefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string full = Path.GetFullPath(Path.Combine(root, hostId, fileId + ".json"));
        if (!full.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)) return false; // belt and braces

        path = full;
        return true;
    }

    private void WriteRecordFile(FileCatalogRecord record)
    {
        if (!TryGetSafeRecordPath(record.HostNodeId, record.FileIdentifier, out var path))
        {
            _logger.LogWarning("[FileCatalog] Refusing to write catalog record with unsafe path components ({Host}/{File}).", record.HostNodeId, record.FileIdentifier);
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            // write-then-move so a crash mid-write can't leave a truncated record behind
            string temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(record, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[FileCatalog] Failed writing catalog record {Host}/{File}.", record.HostNodeId, record.FileIdentifier);
        }
    }

    private void DeleteRecordFile(string hostId, string fileId)
    {
        if (!TryGetSafeRecordPath(hostId, fileId, out var path)) return;

        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { _logger.LogWarning(ex, "[FileCatalog] Failed deleting catalog record {Host}/{File}.", hostId, fileId); }
    }

    private void TryDeleteEmptyHostDirectory(string hostId)
    {
        if (!IsSafePathComponent(hostId)) return;

        try
        {
            string root = GetCatalogRoot();
            string rootPrefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string dir = Path.GetFullPath(Path.Combine(root, hostId));
            if (!dir.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)) return;

            if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
            {
                Directory.Delete(dir);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[FileCatalog] Could not remove empty catalog directory for {Host}.", hostId);
        }
    }

    private void LoadFromDisk()
    {
        string root = GetCatalogRoot();
        if (!Directory.Exists(root)) return;

        int loaded = 0;
        foreach (var hostDir in Directory.EnumerateDirectories(root))
        {
            string hostId = Path.GetFileName(hostDir);
            if (!IsSafePathComponent(hostId)) continue;

            foreach (var file in Directory.EnumerateFiles(hostDir, "*.json"))
            {
                string fileId = Path.GetFileNameWithoutExtension(file);
                if (!IsSafePathComponent(fileId)) continue;

                try
                {
                    var record = JsonSerializer.Deserialize<FileCatalogRecord>(File.ReadAllText(file));
                    if (record == null) continue;

                    // The path is the source of truth for identity, not whatever the file body says.
                    record.HostNodeId = hostId;
                    record.FileIdentifier = fileId;
                    record.CustomFields ??= new Dictionary<string, string>();

                    GetOrCreateBucket(hostId)[fileId] = record;
                    loaded++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[FileCatalog] Skipping unreadable catalog record {File}.", file);
                }
            }
        }

        _logger.LogInformation("[FileCatalog] Loaded {Count} catalog record(s) across {Hosts} host(s) from {Root}.", loaded, _byHost.Count, root);
    }
}
