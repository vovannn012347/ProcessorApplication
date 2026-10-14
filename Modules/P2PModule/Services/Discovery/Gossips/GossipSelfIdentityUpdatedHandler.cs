using System.Text.Json;

using Microsoft.EntityFrameworkCore.Metadata.Internal;
using Microsoft.Extensions.Hosting;

using P2PModule.Infrastructure;
using P2PModule.Interfaces;
using P2PModule.Models;
using P2PModule.Models.Gossips;
using P2PModule.Services.Persistence;

namespace P2PModule.Services.Discovery.Gossip;

/// <summary>
/// Manages topic-based gossip message propagation with a single unified, thread-safe priority queue.
/// Leverages the outer P2PPacketEnvelope.Ttl metadata for dynamic hop decay,
/// protecting the signed GossipMessage payload from mutation-induced signature breakage.
/// </summary>
public class GossipSelfIdentityUpdatedHandler : IHostedService, IDisposable
{
    private readonly IConsensusStore _archiveStore;
    private readonly IChallengeService _challengeService;
    private readonly INodeIdentityService _identity;
    private readonly IP2PGossipService _gossipService;

    //new gossiped nodes
    public GossipSelfIdentityUpdatedHandler(
        IConsensusStore archiveStore,
        IP2PGossipService gossipService,
        IChallengeService challengeService,
        INodeIdentityService identity)
    {
        _identity = identity;
        _gossipService = gossipService;
        _archiveStore = archiveStore;
        _challengeService = challengeService;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _identity.OnIdentityUpdated += _identity_OnIdentityUpdated;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _identity.OnIdentityUpdated -= _identity_OnIdentityUpdated;
        return Task.CompletedTask;
    }

    private async void _identity_OnIdentityUpdated(string previousIdentityDayKey)
    {
        var nodeImmutableData = _identity.GetImmutableData();
        var nodeUpdate = new NodeUpdateData
        {
            Identity = nodeImmutableData
        };

        string todayKey = DateTime.UtcNow.ToString("yyyy-MM-dd");

        var nodeUpdateHash = nodeImmutableData.GetNodeDataHash();
        nodeUpdate.Signatures = _challengeService.GenerateSignatures(nodeUpdateHash).Values.ToList();

        var roleUpdateGossip = new NodeUpdateGossip();
        roleUpdateGossip.Nodes.Add(nodeUpdate);

        await _gossipService.PublishAsync("NodeUpdates", JsonSerializer.Serialize(roleUpdateGossip), 2);
    }

    public void Dispose()
    {
        _identity.OnIdentityUpdated -= _identity_OnIdentityUpdated;
    }

}