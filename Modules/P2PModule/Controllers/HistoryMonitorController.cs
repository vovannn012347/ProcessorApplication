using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using P2PModule.Interfaces;
using P2PModule.Services.Maintenance;

using ProcessorApplication.Attributes;

namespace P2PModule.Controllers;
[ModuleRoute("P2P")]
[Route("/[area]/[controller]/[action]/{id?}")]
[Authorize(Roles = "NetworkAdmin")]
[ApiExplorerSettings(IgnoreApi = true)]
public class HistoryMonitorController : Controller
{
    private readonly IConsensusStore _consensusStore;
    private readonly IConsensusSessionManager _sessionManager;
    private readonly IConsensusProtocolHandler _protocolHandler;

    public HistoryMonitorController(
        IConsensusStore consensusStore,
        IConsensusSessionManager sessionManager,
        IConsensusProtocolHandler protocolHandler)
    {
        _consensusStore = consensusStore;
        _sessionManager = sessionManager;
        _protocolHandler = protocolHandler;
    }

    [HttpGet]
    public IActionResult Index()
    {
        var historicalDays = _consensusStore.GetAvailableHistoricalDays() ?? new List<string>();
        return View(historicalDays);
    }

    /// <summary>
    /// TAB 1: Fetches real-time active consensus session telemetry and visited/queued nodes.
    /// </summary>
    [HttpGet]
    public IActionResult GetActiveSessionState()
    {
        var session = _sessionManager.GetActiveSession();
        if (session == null)
        {
            return Json(new
            {
                isBusy = false,
                sessionId = (Guid?)null,
                targetDate = string.Empty,
                initiator = string.Empty,
                targetNode = string.Empty,
                lastActivity = (DateTime?)null,
                visitedCount = 0,
                queuedCount = 0,
                visitedNodes = new List<string>(),
                queuedNodes = new List<string>()
            });
        }

        return Json(new
        {
            isBusy = true,
            sessionId = session.SessionId,
            targetDate = session.TargetSyncDate,
            initiator = session.InitiatorNodeId,
            targetNode = session.TargetNodeId,
            lastActivity = session.LastActivityUtc,
            visitedCount = session.VisitedNodes.Count,
            queuedCount = session.NodesToVisit.Count,
            visitedNodes = session.VisitedNodes.ToList(),
            queuedNodes = session.NodesToVisit.ToList()
        });
    }


    /// <summary>
    /// TAB 1 ACTION: Triggers manual consensus loop execution on demand.
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> TriggerConsensus()
    {
        try
        {
            bool started = await _protocolHandler.TriggerConsensusAsync();
            if (started)
            {
                return Json(new { success = true, message = "Consensus session triggered successfully." });
            }
            return Json(new { success = false, message = "Failed to trigger consensus - session is already busy." });
        }
        catch (Exception ex)
        {
            return Json(new { success = false, message = $"Error triggering consensus: {ex.Message}" });
        }
    }

    /// <summary>
    /// TAB 1 ACTION: Forces immediate abort and workspace purge of active consensus session.
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AbortConsensus()
    {
        try
        {
            await _sessionManager.AbortSessionAsync();
            return Json(new { success = true, message = "Active consensus session aborted and workspace purged." });
        }
        catch (Exception ex)
        {
            return Json(new { success = false, message = $"Error aborting session: {ex.Message}" });
        }
    }

    /// <summary>
    /// TAB 2: Fetches list of node file records for a selected historical date partition.
    /// </summary>
    [HttpGet]
    public IActionResult GetStagingNodesList(string dateKey)
    {
        var nodeIds = _consensusStore.GetHistoricalPeers(dateKey) ?? new List<string>();
        var viewModel = new HistoricalNodeDetailsViewModel
        {
            TargetDate = dateKey,
            NodeIds = nodeIds
        };

        return PartialView("_StagingNodesList", viewModel);
    }

    ///// <summary>
    ///// TAB 2: Fetches historical node entries for a specific epoch key (yyyy-MM-dd).
    ///// </summary>
    //[HttpGet]
    //public IActionResult GetMergeStagingState(string targetDate)
    //{
    //    if (string.IsNullOrWhiteSpace(targetDate))
    //    {
    //        return BadRequest("Target chronological date key must be provided.");
    //    }

    //    var historicalPeers = _consensusStore.GetHistoricalPeers(targetDate) ?? new List<string>();
    //    var viewModel = new HistoricalNodeDetailsViewModel
    //    {
    //        TargetDate = targetDate,
    //        NodeIds = historicalPeers
    //    };

    //    return PartialView("_StagingNodesList", viewModel);
    //}


    /// <summary>
    /// TAB 2 ACTION: Fetches detailed metadata payload for a specific historical node record file.
    /// </summary>
    [HttpGet]
    public IActionResult GetHistoricalNodeDetails(string date, string nodeId)
    {
        var historicPeer = _consensusStore.GetHistoricalPeer(date, nodeId);
        if (historicPeer == null)
            return NotFound(new { message = $"No historical record found for node {nodeId} on date {date}." });

        var dto = new HistoricalNodeDetailsDto
        {
            NodeId = historicPeer.Identity?.NodeId ?? nodeId,
            TargetDate = date,
            IsVerified = historicPeer.IsVerified,
            TrustScore = historicPeer.TrustScore,
            LastSeen = historicPeer.LastSeen,
            SignatureTime = historicPeer.Identity?.SignatureTime ?? DateTime.MinValue,
            Roles = historicPeer.Identity?.Roles?.ToList() ?? new List<string>(),
            SignalingEndpoints = historicPeer.Identity?.SignalingEndpoints?.ToList() ?? new List<string>()
        };

        return Json(dto);
    }

    /// <summary>
    /// TAB 2 ACTION: Out-of-band integrity evaluation.
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult ForceHealthRecalculation()
    {
        try
        {
            bool isOutdated = _consensusStore.IsOutdated();
            return Json(new { success = true, message = $"Cryptographic validation check complete. Outdated boundary flag: {isOutdated}." });
        }
        catch (Exception ex)
        {
            return Json(new { success = false, message = $"Structural health audit failed: {ex.Message}" });
        }
    }
}

public class HistoricalNodeDetailsViewModel
{
    public string TargetDate { get; set; } = string.Empty;
    public List<string> NodeIds { get; set; } = new();
}

public class HistoricalNodeDetailsDto
{
    public string NodeId { get; set; } = string.Empty;
    public string TargetDate { get; set; } = string.Empty;
    public bool IsVerified { get; set; }
    public double TrustScore { get; set; }
    public DateTime LastSeen { get; set; }
    public DateTime SignatureTime { get; set; }
    public int SignatureCount { get; set; }
    public List<string> Roles { get; set; } = new();
    public List<string> SignalingEndpoints { get; set; } = new();
}