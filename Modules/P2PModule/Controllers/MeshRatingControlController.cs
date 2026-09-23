using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using P2PModule.Interfaces;
using P2PModule.Models;
using P2PModule.Services.Connection;
using P2PModule.Services.Persistence;
using P2PModule.Services.Rating;

using ProcessorApplication.Attributes;

namespace P2PModule.Controllers;

[ModuleRoute("P2P")]
[Route("/[area]/[controller]/[action]/{id?}")]
[Authorize(Roles = "NetworkAdmin")]
[ApiExplorerSettings(IgnoreApi = true)]
public class MeshRatingControlController : Controller
{
    private readonly IPeerConnectionManager _connectionManager;
    private readonly IPeerRatingOverrideService _overrideService;
    private readonly IPeerTracker _peerTracker;
    private readonly IConsensusStore _consensusStore;

    public MeshRatingControlController(
        IPeerConnectionManager connectionManager,
        IPeerRatingOverrideService overrideService,
        IPeerTracker peerTracker,
        IConsensusStore consensusStore)
    {
        _connectionManager = connectionManager;
        _overrideService = overrideService;
        _peerTracker = peerTracker;
        _consensusStore = consensusStore;
    }

    [HttpGet]
    public async Task<IActionResult> Index()
    {
        await _overrideService.LoadRulesAsync();
        var rules = _overrideService.GetRules().ToList();
        return View(rules);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ApplyOverride(string nodeId, bool whitelist, bool blacklist, int? overrideScore)
    {
        if (string.IsNullOrWhiteSpace(nodeId))
        {
            return Json(new { success = false, message = "Target NodeId must be specified." });
        }

        try
        {
            var rule = new PeerOverrideRule
            {
                NodeId = nodeId,
                ForceWhitelist = whitelist,
                ForceBlacklist = blacklist,
                OverrideTrustScore = overrideScore,
                CreatedAt = DateTime.UtcNow,
                AdminNotes = $"Forced operational intervention via UI panel at {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC."
            };

            await _overrideService.ApplyOverrideAsync(rule);

            // If forced blacklist is requested, drop the active lane instantly out-of-band
            if (blacklist)
            {
                await _connectionManager.KillConnectionAsync(nodeId, "Administrative forced blacklist rule applied.");
            }

            return Json(new { success = true, message = $"Administrative rating rule assigned for node {nodeId}." });
        }
        catch (Exception ex)
        {
            return Json(new { success = false, message = $"Failed to register overwrite context bounds: {ex.Message}" });
        }
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RemoveOverride(string nodeId)
    {
        if (string.IsNullOrWhiteSpace(nodeId))
        {
            return Json(new { success = false, message = "Target NodeId must be specified." });
        }

        try
        {
            await _overrideService.RemoveOverrideAsync(nodeId);
            return Json(new { success = true, message = $"Administrative bypass rule evicted for node {nodeId}." });
        }
        catch (Exception ex)
        {
            return Json(new { success = false, message = $"Override removal transaction failed: {ex.Message}" });
        }
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DropRedundantConnection(string nodeId)
    {
        if (string.IsNullOrWhiteSpace(nodeId))
        {
            return Json(new { success = false, message = "Invalid peer reference." });
        }

        try
        {
            bool detached = await _connectionManager.KillConnectionAsync(nodeId, "Administrative out-of-band session termination triggered.");
            if (detached)
            {
                return Json(new { success = true, message = $"WebRTC tunnel severed successfully for target: {nodeId}." });
            }
            return Json(new { success = false, message = "No active transport socket channel detected for this node identifier." });
        }
        catch (Exception ex)
        {
            return Json(new { success = false, message = $"Socket teardown exception logged: {ex.Message}" });
        }
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ForgetNode(string nodeId)
    {
        if (string.IsNullOrWhiteSpace(nodeId))
        {
            return Json(new { success = false, message = "Target NodeId string bounds must be provided." });
        }

        try
        {
            // 1. Sever any active transport slots out-of-band first
            var runtimePeer = _peerTracker.GetPeer(nodeId, historic: false);
            if (runtimePeer != null)
            {
                await _connectionManager.KillConnectionAsync(nodeId, "Forced administrative node eviction pipeline triggered.");
            }


            // 3. Loop over and explicitly remove the node from all epoch-based daily shards inside historical records
            List<string> historicalDays = _peerTracker.GetPeerIndex(nodeId)?.IndexEpochs.ToList() ?? new();
            if (historicalDays != null && historicalDays.Any())
            {
                foreach (string dayKey in historicalDays)
                {
                    List<string> dayPeers = _consensusStore.GetHistoricalPeers(dayKey);
                    if (dayPeers != null && dayPeers.Contains(nodeId))
                    {
                        // Overwrite/Evict the record for that specific date shard matrix block
                        _consensusStore.UpdateHistoricalPeer(dayKey, nodeId, null!);
                    }
                }
            }

            // 2. Explicitly wipe from volatile tracking state layers (Recent Peers cache pool)

            _peerTracker.RemovePeer(nodeId);

            // 4. Force immediate persistence flushes out to physical disk shards
            await _peerTracker.FlushPendingUpdatesToDiskAsync();

            return Json(new { success = true, message = $"Node {nodeId} completely forgotten from active tracking and historic epoch shards." });
        }
        catch (Exception ex)
        {
            return Json(new { success = false, message = $"Administrative node pruning transaction dropped: {ex.Message}" });
        }
    }
}