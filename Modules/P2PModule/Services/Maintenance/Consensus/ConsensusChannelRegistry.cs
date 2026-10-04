using Common.Interfaces.Consensus;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using P2PModule.Configuration;

namespace P2PModule.Services.Maintenance;

public interface IConsensusChannelRegistry
{
    /// <summary>All channels currently INSTALLED (registered by some module), regardless of whether
    /// the user has opted to actively advertise them. Useful for admin UI ("installed but off").</summary>
    IReadOnlyCollection<IConsensusChannel> All { get; }

    /// <summary>Installed channel ids, unfiltered.</summary>
    IReadOnlyCollection<string> AllChannelIds { get; }

    /// <summary>
    /// Channel ids the user has actively opted to advertise (P2PSettings.AdvertisedConsensusChannels
    /// intersected with what's actually installed). This - not AllChannelIds - is what gets gossiped
    /// in this node's identity. Re-evaluated live against current settings on every call, so a
    /// settings change takes effect on the very next identity refresh without a restart.
    /// </summary>
    IReadOnlyCollection<string> AdvertisedChannelIds { get; }

    IConsensusChannel? Get(string channelId);
}

/// <summary>
/// Aggregates every IConsensusChannel implementation registered by any module's ConfigureServices
/// into a single lookup, the same pattern AdvertisedCapabilitiesRegistry uses for module capabilities.
/// Because module registration happens against one shared IServiceCollection (see Program.cs), a
/// channel registered by e.g. FileTransferModule is picked up here automatically - this class
/// doesn't need to know which modules exist.
/// </summary>
public class ConsensusChannelRegistry : IConsensusChannelRegistry
{
    private readonly Dictionary<string, IConsensusChannel> _byId;
    private readonly IOptionsMonitor<P2PSettings> _p2pSettings;

    public IReadOnlyCollection<IConsensusChannel> All => _byId.Values;
    public IReadOnlyCollection<string> AllChannelIds => _byId.Keys;

    public IReadOnlyCollection<string> AdvertisedChannelIds
    {
        get
        {
            var enabled = _p2pSettings.CurrentValue.AdvertisedConsensusChannels;
            if (enabled == null || enabled.Count == 0)
                return Array.Empty<string>();

            return _byId.Keys
                .Where(id => enabled.Contains(id, StringComparer.OrdinalIgnoreCase))
                .ToList();
        }
    }

    public ConsensusChannelRegistry(IEnumerable<IConsensusChannel> channels, IOptionsMonitor<P2PSettings> p2pSettings, ILogger<ConsensusChannelRegistry> logger)
    {
        _p2pSettings = p2pSettings;
        _byId = new Dictionary<string, IConsensusChannel>(StringComparer.OrdinalIgnoreCase);

        foreach (var channel in channels)
        {
            if (string.IsNullOrWhiteSpace(channel.ChannelId))
            {
                logger.LogWarning("[ConsensusChannelRegistry] Ignoring IConsensusChannel of type {Type} with empty ChannelId.", channel.GetType().FullName);
                continue;
            }

            if (!_byId.TryAdd(channel.ChannelId, channel))
            {
                logger.LogWarning("[ConsensusChannelRegistry] Duplicate consensus ChannelId '{ChannelId}' registered by {Type} - keeping the first registration.",
                    channel.ChannelId, channel.GetType().FullName);
            }
        }

        logger.LogInformation("[ConsensusChannelRegistry] {Count} consensus channel(s) installed: {Ids}",
            _byId.Count, string.Join(", ", _byId.Keys));
    }

    public IConsensusChannel? Get(string channelId)
        => !string.IsNullOrWhiteSpace(channelId) && _byId.TryGetValue(channelId, out var channel) ? channel : null;
}
