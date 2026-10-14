using Common.Interfaces.Consensus;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using P2PModule.Configuration;

using ProcessorApplication.Utils;

namespace P2PModule.Services.Maintenance;

/// <summary>
/// What changed in this node's advertised channel set. Only raised when the set really differs.
/// All lists are sorted (ordinal, case-insensitive) and never null.
/// </summary>
public sealed record AdvertisedChannelsChange(
    IReadOnlyList<string> Previous,
    IReadOnlyList<string> Current,
    IReadOnlyList<string> Added,
    IReadOnlyList<string> Removed);

public interface IConsensusChannelRegistry
{
    /// <summary>All channels currently INSTALLED (registered by some module), regardless of whether
    /// the user has opted to actively advertise them. Useful for admin UI ("installed but off").</summary>
    IReadOnlyCollection<IConsensusChannel> All { get; }

    /// <summary>Installed channel ids, unfiltered.</summary>
    IReadOnlyCollection<string> AllChannelIds { get; }

    /// <summary>
    /// Channel ids this node advertises: P2PSettings.AdvertisedConsensusChannels intersected with
    /// what's actually installed, sorted. This - not AllChannelIds - is what gets gossiped in this
    /// node's identity, and what channel sync accepts/initiates for.
    ///
    /// It is a cached snapshot, and those are its ONLY two inputs: the setting (the user's choice)
    /// and module availability (installed channels are fixed for the process lifetime). It is
    /// recomputed only when P2P settings change, so every reader - identity, channel sync, UI -
    /// sees the same answer between changes.
    /// </summary>
    IReadOnlyCollection<string> AdvertisedChannelIds { get; }

    /// <summary>
    /// Raised (on a background thread) after a settings change actually altered the advertised
    /// set. Not raised for saves that leave it the same. Handlers must be quick and non-throwing.
    /// </summary>
    event Action<AdvertisedChannelsChange>? AdvertisedChannelsChanged;

    IConsensusChannel? Get(string channelId);
}

/// <summary>
/// Aggregates every IConsensusChannel implementation registered by any module's ConfigureServices
/// into a single lookup, the same pattern AdvertisedCapabilitiesRegistry uses for module capabilities.
/// Because module registration happens against one shared IServiceCollection (see Program.cs), a
/// channel registered by e.g. FileTransferModule is picked up here automatically - this class
/// doesn't need to know which modules exist.
///
/// Channels are resolved LAZILY, on first use - not in the constructor. Channels come from other
/// modules and depend on those modules' services, which in turn depend on core services such as
/// INodeIdentityService - and NodeIdentityService itself depends on this registry. Resolving
/// IEnumerable&lt;IConsensusChannel&gt; in the constructor therefore closed a loop:
///   NodeIdentityService -> ConsensusChannelRegistry -> FileCatalogConsensusChannel
///     -> IFileArchiveService / INodeIdentityService -> NodeIdentityService (still being built)
/// The channels are registered via factory lambdas, which hides that loop from the DI container's
/// circular-dependency check, so instead of a clear error startup recursed/hung in PrestartInit.
/// Deferring resolution means the registry is cheap to construct and, by the time anything reads
/// it, NodeIdentityService already exists.
/// </summary>
public class ConsensusChannelRegistry : IConsensusChannelRegistry, IDisposable
{
    private readonly Lazy<Dictionary<string, IConsensusChannel>> _byIdLazy;
    private readonly IOptionsMonitor<P2PSettings> _p2pSettings;
    private readonly ILogger<ConsensusChannelRegistry> _logger;
    private readonly IDisposable _settingsListener;

    private readonly object _snapshotLock = new();
    private volatile IReadOnlyList<string>? _advertisedSnapshot;

    public event Action<AdvertisedChannelsChange>? AdvertisedChannelsChanged;

    private Dictionary<string, IConsensusChannel> _byId => _byIdLazy.Value;

    public IReadOnlyCollection<IConsensusChannel> All => _byId.Values;
    public IReadOnlyCollection<string> AllChannelIds => _byId.Keys;

    public IReadOnlyCollection<string> AdvertisedChannelIds => _advertisedSnapshot ?? EnsureSnapshot();

    public ConsensusChannelRegistry(IServiceProvider serviceProvider, IOptionsMonitor<P2PSettings> p2pSettings, ILogger<ConsensusChannelRegistry> logger)
    {
        _p2pSettings = p2pSettings;
        _logger = logger;

        // ExecutionAndPublication: built exactly once even if several threads hit it first. If a
        // channel's own construction ever reads this registry again, Lazy throws a clear
        // InvalidOperationException instead of silently recursing.
        _byIdLazy = new Lazy<Dictionary<string, IConsensusChannel>>(
            () => BuildChannelMap(serviceProvider.GetServices<IConsensusChannel>(), logger),
            LazyThreadSafetyMode.ExecutionAndPublication);

        // Debounced: one settings save produces several change notifications; recompute once.
        _settingsListener = p2pSettings.OnChangeDebounced(_ => RefreshAdvertisedSnapshot());
    }

    private IReadOnlyList<string> EnsureSnapshot()
    {
        lock (_snapshotLock)
        {
            return _advertisedSnapshot ??= ComputeAdvertised();
        }
    }

    /// <summary>Setting ∩ installed, sorted. The only place the advertised set is derived.</summary>
    private IReadOnlyList<string> ComputeAdvertised()
    {
        var enabled = _p2pSettings.CurrentValue.AdvertisedConsensusChannels;
        if (enabled == null || enabled.Count == 0) return Array.Empty<string>();

        var installed = _byId;
        var notInstalled = enabled
            .Where(id => !string.IsNullOrWhiteSpace(id) && !installed.ContainsKey(id))
            .ToList();
        if (notInstalled.Count > 0)
        {
            _logger.LogInformation("[ConsensusChannelRegistry] Opted-in channel(s) not installed on this node, not advertised: {Ids}",
                string.Join(", ", notInstalled));
        }

        return installed.Keys
            .Where(id => enabled.Contains(id, StringComparer.OrdinalIgnoreCase))
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToList()
            .AsReadOnly();
    }

    private void RefreshAdvertisedSnapshot()
    {
        AdvertisedChannelsChange? change = null;

        lock (_snapshotLock)
        {
            var previous = _advertisedSnapshot;

            // Nobody has read the advertised set yet -> nobody acted on it (nothing gossiped, no
            // walks started). Just drop the cache; the first reader computes it fresh. This also
            // keeps a settings reload during startup from forcing channel resolution early.
            if (previous == null)
            {
                return;
            }

            var current = ComputeAdvertised();
            if (previous.SequenceEqual(current, StringComparer.OrdinalIgnoreCase))
            {
                return; // a save that didn't touch the advertised set - no event, no identity rebuild
            }

            _advertisedSnapshot = current;
            change = new AdvertisedChannelsChange(
                previous,
                current,
                current.Except(previous, StringComparer.OrdinalIgnoreCase).ToList(),
                previous.Except(current, StringComparer.OrdinalIgnoreCase).ToList());
        }

        _logger.LogInformation("[ConsensusChannelRegistry] Advertised channels changed: [{Previous}] -> [{Current}] (added: {Added}; removed: {Removed})",
            string.Join(", ", change.Previous), string.Join(", ", change.Current),
            change.Added.Count == 0 ? "none" : string.Join(", ", change.Added),
            change.Removed.Count == 0 ? "none" : string.Join(", ", change.Removed));

        var handlers = AdvertisedChannelsChanged?.GetInvocationList();
        if (handlers == null) return;
        foreach (Action<AdvertisedChannelsChange> handler in handlers)
        {
            try { handler(change); }
            catch (Exception ex) { _logger.LogWarning(ex, "[ConsensusChannelRegistry] AdvertisedChannelsChanged handler threw."); }
        }
    }

    private static Dictionary<string, IConsensusChannel> BuildChannelMap(IEnumerable<IConsensusChannel> channels, ILogger logger)
    {
        var byId = new Dictionary<string, IConsensusChannel>(StringComparer.OrdinalIgnoreCase);

        foreach (var channel in channels)
        {
            if (string.IsNullOrWhiteSpace(channel.ChannelId))
            {
                logger.LogWarning("[ConsensusChannelRegistry] Ignoring IConsensusChannel of type {Type} with empty ChannelId.", channel.GetType().FullName);
                continue;
            }

            if (!byId.TryAdd(channel.ChannelId, channel))
            {
                logger.LogWarning("[ConsensusChannelRegistry] Duplicate consensus ChannelId '{ChannelId}' registered by {Type} - keeping the first registration.",
                    channel.ChannelId, channel.GetType().FullName);
            }
        }

        logger.LogInformation("[ConsensusChannelRegistry] {Count} consensus channel(s) installed: {Ids}",
            byId.Count, string.Join(", ", byId.Keys));

        return byId;
    }

    public IConsensusChannel? Get(string channelId)
        => !string.IsNullOrWhiteSpace(channelId) && _byId.TryGetValue(channelId, out var channel) ? channel : null;

    public void Dispose() => _settingsListener.Dispose();
}
