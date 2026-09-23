using System.Net.Http.Json;
using System.Text;

using Common.Code;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using P2PModule.Interfaces;
using P2PModule.Models;
using P2PModule.Services.Connection;
using P2PModule.Services.Persistence;
using P2PModule.Services.Rating;

using ProcessorApplication.Attributes;

namespace P2PModule.Controllers;

public class MeshPeerDisplayItem
{
    public string NodeId { get; set; } = string.Empty;
    public List<string> Roles { get; set; } = new();
    public List<string> Endpoints { get; set; } = new();
    public double NormalTrustScore { get; set; }
    public double EffectiveTrustScore { get; set; }
    public double MaxTrustScore { get; set; } = 100.0;
    public bool IsConnected { get; set; }
    public bool IsHandshaked { get; set; }
    public bool IsVerified { get; set; }
    public List<string> ConnectionTypes { get; set; } = new();
    public DateTime LastSeenOrActiveUtc { get; set; }
}

[ModuleRoute("P2P")]
[Route("/[area]/[controller]/[action]/{id?}")]
[Authorize(Roles = "NetworkAdmin")]
[ApiExplorerSettings(IgnoreApi = true)]
public class MeshControlController : Controller
{
    private readonly IPeerTracker _peerTracker;
    private readonly IRatingService _ratingService; 
    private readonly INodeIdentityService _identityService;
    private readonly IPeerConnectionManager _connectionManager;
    private readonly IHttpClientFactory _httpClientFactory;

    public MeshControlController(
        IPeerTracker peerTracker,
        IRatingService ratingService,
        IPeerConnectionManager connectionManager,
        INodeIdentityService identityService,
        IHttpClientFactory httpClientFactory)
    {
        _peerTracker = peerTracker;
        _ratingService = ratingService;
        _connectionManager = connectionManager;
        _identityService = identityService;
        _httpClientFactory = httpClientFactory;
    }

    [HttpGet]
    public IActionResult Index()
    {
        var models = FetchOverviewList();

        if (Request.Headers["X-Requested-With"] == "XMLHttpRequest")
        {
            return PartialView("_NodeTableRows", models);
        }

        byte[]? pubKey = _identityService.GetPublicKey();
        byte[]? verifyKey = _identityService.GetPublicVerificator();

        ViewData["LocalNodeId"] = _identityService.GetLocalNodeId();
        var roles = string.Join(", ", _identityService.GetCapablities() ?? new List<string>() { });
        ViewData["LocalNodeRoles"] = string.IsNullOrWhiteSpace(roles) ? "-" : roles;

        ViewData["LocalEncryptionKey"] = pubKey != null ? Convert.ToBase64String(pubKey) : "N/A";
        ViewData["LocalVerifyKey"] = verifyKey != null ? Convert.ToBase64String(verifyKey) : "N/A";

        return View(models);
    }

    private List<MeshPeerDisplayItem> FetchOverviewList()
    {
        var activeConnections = _connectionManager.GetActiveConnections()
            .ToDictionary(c => c.NodeId, StringComparer.OrdinalIgnoreCase);

        var lastSeenPeers = _peerTracker.GetLastSeenNodes(PeerTracker.DefaultNodeComparer).ToList();

        var allNodeIds = new HashSet<string>(activeConnections.Keys, StringComparer.OrdinalIgnoreCase);
        foreach (var peer in lastSeenPeers)
        {
            if (peer?.Identity?.NodeId != null)
            {
                allNodeIds.Add(peer.Identity.NodeId);
            }
        }

        var resultList = new List<MeshPeerDisplayItem>();

        foreach (var nodeId in allNodeIds)
        {
            activeConnections.TryGetValue(nodeId, out var conn);
            var runtimePeer = _peerTracker.GetPeer(nodeId);

            var roles = runtimePeer?.Identity?.Roles ?? new List<string>();
            var endpoints = runtimePeer?.Identity?.SignalingEndpoints ?? new List<string>();

            double normalScore = runtimePeer?.TrustScore ?? 0.0;
            double effectiveScore = _ratingService.GetEffectiveTrustScore(nodeId, applyOverrides: true);

            DateTime connActivity = conn != null ? (conn.LastActivity > conn.ConnectedAt ? conn.LastActivity : conn.ConnectedAt) : DateTime.MinValue;
            DateTime peerLastSeen = runtimePeer?.LastSeen ?? DateTime.MinValue;
            DateTime mostRecentUtc = conn != null ? connActivity : peerLastSeen;

            bool isConnected = conn != null && conn.PeerConnection != null && conn.PeerConnection.connectionState == SIPSorcery.Net.RTCPeerConnectionState.connected;

            var displayItem = new MeshPeerDisplayItem
            {
                NodeId = nodeId,
                Roles = roles.ToList(),
                Endpoints = endpoints.ToList(),
                NormalTrustScore = normalScore,
                EffectiveTrustScore = effectiveScore,
                MaxTrustScore = 100.0,
                IsConnected = isConnected,
                IsHandshaked = conn?.IsHandshaked ?? false,
                IsVerified = runtimePeer?.IsVerified ?? false,
                ConnectionTypes = conn?.ConnectionTypes?.ToList() ?? new List<string>(),
                LastSeenOrActiveUtc = mostRecentUtc
            };

            resultList.Add(displayItem);
        }

        return resultList.OrderByDescending(x => x.LastSeenOrActiveUtc).ToList();
    }

    /// <summary>
    /// Helper method to write a formatted SSE line directly to Kestrel's output body stream.
    /// </summary>
    private async Task WriteSseLineAsync(string message, CancellationToken cancellationToken)
    {
        var data = $"data: {DateTime.Now:HH:mm:ss} | {message}\n\n";
        await Response.Body.WriteAsync(Encoding.UTF8.GetBytes(data), cancellationToken);
        await Response.Body.FlushAsync(cancellationToken);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ChangeIdentity()
    {
        var cancellationToken = HttpContext.RequestAborted;

        _identityService.UpdateDailyKeys(true);

        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task ConnectByHttp(string rawUrls)
    {
        var cancellationToken = HttpContext.RequestAborted;

        Response.ContentType = "text/event-stream";
        Response.Headers.Add("Cache-Control", "no-cache");
        Response.Headers.Add("Connection", "keep-alive");

        if (string.IsNullOrWhiteSpace(rawUrls))
        {
            await WriteSseLineAsync("[ERROR] Empty targets list received.", cancellationToken);
            await WriteSseLineAsync("[DONE]", cancellationToken);
            return;
        }

        var urls = rawUrls.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries)
                          .Select(u => u.Trim()).Where(u => !string.IsNullOrEmpty(u)).ToList();

        await WriteSseLineAsync($"HTTP Batch Connection launched for {urls.Count} entries.", cancellationToken);

        using var httpClient = _httpClientFactory.CreateClient();
        httpClient.Timeout = TimeSpan.FromSeconds(5);

        foreach (var url in urls)
        {
            if (cancellationToken.IsCancellationRequested) break;

            var baseEndpoint = url.TrimEnd('/');
            await WriteSseLineAsync($"[PROBE] Checking connectivity baseline for: {baseEndpoint}", cancellationToken);
            try
            {
                //var activeEndpoint = await _connectionManager.GetEndpointActiveAsync(baseEndpoint);
                //if (!string.IsNullOrEmpty(activeEndpoint))
                //{
                //    await WriteSseLineAsync($"[ACTIVE] Endpoint returned confirmed PONG. Requesting identity payload...", cancellationToken);

                    string identityUrl = $"{baseEndpoint}/p2p/identity";
                    var identityResponse = await httpClient.GetHttpAsync(identityUrl, ct: cancellationToken);

                    if (identityResponse != null && identityResponse.IsSuccessStatusCode)
                    {
                        var identityData = await identityResponse.Content.ReadFromJsonAsync<PeerImmutableData>(cancellationToken: cancellationToken);

                        if (identityData != null && !string.IsNullOrEmpty(identityData.NodeId))
                        {
                            await WriteSseLineAsync($"[IDENTITY] Resolved Node ID: {identityData.NodeId}. Initiating WebRTC Handshake...", cancellationToken);

                            // Trigger peer connection with the dynamically discovered identity details
                            var activeConn = await _connectionManager.ConnectPeerAsync(
                                identityData.NodeId,
                                identityData.SignalingEndpoints?.ToArray() ?? new[] { baseEndpoint },
                                "outbound"
                            );

                            if (activeConn != null)
                            {
                                await WriteSseLineAsync($"[CONNECTED] Established WebRTC Direct Tunnel with node {identityData.NodeId}.", cancellationToken);
                            }
                            else
                            {
                                await WriteSseLineAsync($"[FAILED] WebRTC direct signaling handshake timed out or reputation check dropped for: {identityData.NodeId}", cancellationToken);
                            }
                        }
                        else
                        {
                            await WriteSseLineAsync($"[ERROR] Received malformed identity payload from {url}", cancellationToken);
                        }
                    }
                    else
                    {
                        await WriteSseLineAsync($"[ERROR] Identity request failed with HTTP status: {identityResponse.StatusCode}", cancellationToken);
                    }
                //}
                //else
                //{
                //    await WriteSseLineAsync($"[TIMEOUT] Endpoint {url} failed active ping verification.", cancellationToken);
                //}
            }
            catch (Exception ex)
            {
                await WriteSseLineAsync($"[EXCEPTION] Connection/handshake protocol error on {url}: {ex.Message}", cancellationToken);
            }
        }

        await WriteSseLineAsync("[DONE]", cancellationToken);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task ConnectByNodeId(string rawIds)
    {
        var cancellationToken = HttpContext.RequestAborted;

        Response.ContentType = "text/event-stream";
        Response.Headers.Add("Cache-Control", "no-cache");
        Response.Headers.Add("Connection", "keep-alive");

        if (string.IsNullOrWhiteSpace(rawIds))
        {
            await WriteSseLineAsync("[ERROR] Empty targets list received.", cancellationToken);
            await WriteSseLineAsync("[DONE]", cancellationToken);
            return;
        }

        var ids = rawIds.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(i => i.Trim()).Where(i => !string.IsNullOrEmpty(i)).ToList();

        await WriteSseLineAsync($"WebRTC direct routing lane mapping launched for {ids.Count} target IDs.", cancellationToken);

        foreach (var id in ids)
        {
            if (cancellationToken.IsCancellationRequested) break;

            await WriteSseLineAsync($"[SIGNAL] Initiating connection handshake path for Station Id: {id}", cancellationToken);
            try
            {
                // Passing empty signalingEndpoints array to initiate direct WebRTC-first fallback signaling pathways
                var activeConn = await _connectionManager.ConnectPeerAsync(id, Array.Empty<string>(), "outbound");
                if (activeConn != null)
                {
                    await WriteSseLineAsync($"[SUCCESS] WebRTC Direct Tunnel lane locked open for Station: {id}", cancellationToken);
                }
                else
                {
                    await WriteSseLineAsync($"[FAILED] Signaling routing trace dropped or reputation validation invalid for: {id}", cancellationToken);
                }
            }
            catch (Exception ex)
            {
                await WriteSseLineAsync($"[EXCEPTION] Signaling protocol exception for node {id}: {ex.Message}", cancellationToken);
            }
        }

        await WriteSseLineAsync("[DONE]", cancellationToken);
    }
}