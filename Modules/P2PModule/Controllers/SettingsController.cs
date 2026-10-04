using Common.Interfaces;
using Common.Models;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

using P2PModule.Configuration;
using P2PModule.Controllers.ViewModels;

using ProcessorApplication;
using ProcessorApplication.Attributes;
using ProcessorApplication.Configuration.Settings;
using ProcessorApplication.Utils;

using SIPSorcery.Media.G729Codec;

namespace P2PModule.Controllers;

/// <summary>
/// Exposes manual actions to modular administrators to override node ratings and close connections.
/// </summary>
[ModuleRoute("P2P")]
[Route("/[area]/[controller]/[action]/{id?}")]
[Authorize(Policy = "AdminLocalPolicy", Roles = "Admin")]
[ApiExplorerSettings(IgnoreApi = true)]
public class SettingsController : Controller
{
    public string ModuleId => P2PModuleEntryPoint.MODULE_ID;

    private readonly ISettingService _settingsService;
    private readonly IOptionsMonitor<P2PSettings> _generalSettings;
    private readonly IOptionsMonitor<StorageSettings> _storageSettings;
    private readonly IOptionsMonitor<GossipSettings> _gossipSandbox;
    private readonly IOptionsMonitor<RatingSettings> _ratingService;
    private readonly IEnumerable<IEndpointProvider> _endpointProviders;
    private readonly AdvertisedCapabilitiesRegistry _capabilities;

    public SettingsController(
        ISettingService settingsService,
        IEnumerable<IEndpointProvider> endpointProviders,
        AdvertisedCapabilitiesRegistry capabilities,
        IOptionsMonitor<P2PSettings> general,
        IOptionsMonitor<StorageSettings> python,
        IOptionsMonitor<GossipSettings> none,
        IOptionsMonitor<RatingSettings> osSandbox)
    {
        _settingsService = settingsService;
        _endpointProviders = endpointProviders;
        _capabilities = capabilities;
        _generalSettings = general;
        _storageSettings = python;
        _gossipSandbox = none;
        _ratingService = osSandbox;
    }

    public IActionResult Index()
    {
        var discoverySources = _endpointProviders
            .SelectMany(p => p.GetEndpointSources())
            .Distinct()
            .ToList();


        var model = new P2PSettingsViewModel
        {
            EndpointProvisions = discoverySources,
            AvailableRoles = _capabilities.All,
            General = _generalSettings.CurrentValue,
            Storage = _storageSettings.CurrentValue,
            Gossip = _gossipSandbox.CurrentValue,
            Rating = _ratingService.CurrentValue
        };

        if (Request.IsAjaxRequest())
        {
            return PartialView(model);
        }

        return View(model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveP2P(P2PSettingsViewModel model)
    {
        if (model == null)
        {
            ModelState.AddModelError(string.Empty, "Invalid configuration data block supplied.");
            return PartialView("Index", model);
        }

        ModelState.Remove(nameof(model.EndpointProvisions));

        // Isolated validation orchestration
        ValidateGeneral(model.General);
        ValidateStorage(model.Storage);
        ValidateGossip(model.Gossip);
        ValidateRating(model.Rating);

        if (!ModelState.IsValid)
        {
            return PartialView("Index", model);
        }

        try
        {
            _settingsService.SetAutoUpdate(false);
            // Segmented conditional persistence execution 
            await ProcessSaveGeneral(model.General);
            await ProcessSaveStorage(model.Storage);
            await ProcessSaveGossip(model.Gossip);
            await ProcessSaveRating(model.Rating);

            _settingsService.SetAutoUpdate(true);
            _settingsService.ForceUpdateOptionsMonitor();

            TempData["SuccessMessage"] = "P2P network module execution matrix bounds committed successfully.";
        }
        catch (Exception ex)
        {
            ModelState.AddModelError(string.Empty, $"Critical architecture disk commit failure: {ex.Message}");
        }

        return PartialView("Index", model);
    }


    [HttpGet]
    public IActionResult ResolveDiscoveryEndpoint(string actionKey)
    {
        if (string.IsNullOrEmpty(actionKey))
            return Json(new { endpoint = string.Empty });

        // Safely queries our cached providers list
        var handlingProvider = _endpointProviders.FirstOrDefault(p =>
            p.GetEndpointSources().Contains(actionKey, StringComparer.OrdinalIgnoreCase));

        if (handlingProvider != null)
        {
            var endpoints = handlingProvider.ProvideEndpoints(actionKey);
            var targetedEndpoint = endpoints.FirstOrDefault();

            if (!string.IsNullOrEmpty(targetedEndpoint))
            {
                return Json(new { endpoint = targetedEndpoint });
            }
        }

        return Json(new { endpoint = string.Empty });
    }

    #region Validation Subroutines

    private void ValidateGeneral(P2PSettings model)
    {
        if (model == null)
        {
            ModelState.AddModelError(string.Empty, "General network identity settings segment cannot be empty.");
            return;
        }

        if (string.IsNullOrWhiteSpace(model.DataStoragePath))
        {
            ModelState.AddModelError($"General.{nameof(model.DataStoragePath)}", "Data storage directory path is required.");
        }

        if (model.TargetOutboundConnections < 1 || model.TargetOutboundConnections > 50)
        {
            ModelState.AddModelError($"General.{nameof(model.TargetOutboundConnections)}", "Target outbound connections must settle between 1 and 50 paths.");
        }

        if (model.StartupDelaySeconds < 0 || model.StartupDelaySeconds > 300)
        {
            ModelState.AddModelError($"General.{nameof(model.StartupDelaySeconds)}", "Startup initialization delay bounds are constrained between 0 and 300 seconds.");
        }

        if (model.ActiveNodeSecondsBackupPeriod < 60 || model.ActiveNodeSecondsBackupPeriod > 86400)
        {
            ModelState.AddModelError($"General.{nameof(model.ActiveNodeSecondsBackupPeriod)}", "Backup synchronization window period must span between 60 seconds and 24 hours.");
        }

        if (model.TopologyMaintenannceIntervalMs < 5000)
        {
            ModelState.AddModelError($"General.{nameof(model.TopologyMaintenannceIntervalMs)}", "Topology maintenance evaluation loop thread delay cannot drop below 5000ms.");
        }

        if (model.HttpTimeoutSeconds < 1 || model.HttpTimeoutSeconds > 300)
            ModelState.AddModelError($"General.{nameof(model.HttpTimeoutSeconds)}", "HTTP API Timeout is restricted between 1 and 300 seconds.");

        if (model.DefaultHandshakeTimeoutSeconds < 1 || model.DefaultHandshakeTimeoutSeconds > 300)
            ModelState.AddModelError($"General.{nameof(model.DefaultHandshakeTimeoutSeconds)}", "Default Handshake Timeout is restricted between 1 and 300 seconds.");

        if (model.DefaultRequestTimeoutSeconds < 1 || model.DefaultRequestTimeoutSeconds > 120)
            ModelState.AddModelError($"General.{nameof(model.DefaultRequestTimeoutSeconds)}", "Default Request Timeout is restricted between 1 and 120 seconds.");
    }

    private void ValidateStorage(StorageSettings model)
    {
        if (model == null)
        {
            ModelState.AddModelError(string.Empty, "Persistence configuration section properties cannot be empty.");
            return;
        }

        if (model.PersistTimePeriodSeconds < 5)
        {
            ModelState.AddModelError($"Storage.{nameof(model.PersistTimePeriodSeconds)}", "Persistent physical write cycle interval cannot drop below a 5-second floor.");
        }

        if (model.RetainedHistoryEpochsCount < 1)
        {
            ModelState.AddModelError($"Storage.{nameof(model.RetainedHistoryEpochsCount)}", "Retained core chronological ledger epochs count floor boundary must evaluate to at least 1.");
        }
    }

    private void ValidateGossip(GossipSettings model)
    {
        if (model == null)
        {
            ModelState.AddModelError(string.Empty, "Gossip discovery parameters section cannot be empty.");
            return;
        }

        if (model.MaxNewNodesDrip < 1)
        {
            ModelState.AddModelError($"Gossip.{nameof(model.MaxNewNodesDrip)}", "Anti-Dos ingress queue limit metric requires at least 1 node tracking target capacity.");
        }

        if (model.FanoutTargetCount < 1 || model.FanoutTargetCount > 20)
        {
            ModelState.AddModelError($"Gossip.{nameof(model.FanoutTargetCount)}", "Epidemic state relay distribution fanout is bounded between 1 and 20 target endpoints.");
        }
    }

    private void ValidateRating(RatingSettings model)
    {
        if (model == null)
        {
            ModelState.AddModelError(string.Empty, "Trust assessment coefficients matrix section cannot be empty.");
            return;
        }

        if (model.ConsensusSelfTrust < 0.0 || model.ConsensusSelfTrust > 1.0)
        {
            ModelState.AddModelError($"Rating.{nameof(model.ConsensusSelfTrust)}", "Subjective index identity weight ratio must evaluate directly within continuous bounds 0.0 and 1.0.");
        }
    }

    #endregion

    #region Persistence Subroutines

    private async Task ProcessSaveGeneral(P2PSettings model)
    {
        if (model == null) return;
        var endpoints = model.Endpoints.FirstOrDefault() ?? string.Empty;
        model.Endpoints = endpoints.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .ToList();
        await _settingsService.SetAsync(ModuleId, $"{nameof(P2PSettings)}:{nameof(model.Endpoints)}", model.Endpoints);
        
        await _settingsService.SetAsync(ModuleId, $"{nameof(P2PSettings)}:{nameof(model.NodeRoles)}", model.NodeRoles);
        // Persist temporary tracker flag state
        await _settingsService.SetAsync(ModuleId, $"{nameof(P2PSettings)}:{nameof(model.IsTemporarilyPromotedTracker)}", model.IsTemporarilyPromotedTracker);

        await _settingsService.SetAsync(ModuleId, $"{nameof(P2PSettings)}:{nameof(model.DataStoragePath)}", model.DataStoragePath);
        await _settingsService.SetAsync(ModuleId, $"{nameof(P2PSettings)}:{nameof(model.StartupDelaySeconds)}", model.StartupDelaySeconds);
        await _settingsService.SetAsync(ModuleId, $"{nameof(P2PSettings)}:{nameof(model.TargetOutboundConnections)}", model.TargetOutboundConnections);

        var trackers = model.SeedEndpoints.FirstOrDefault() ?? string.Empty;
        model.SeedEndpoints = trackers.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .ToList();
        await _settingsService.SetAsync(ModuleId, $"{nameof(P2PSettings)}:{nameof(model.SeedEndpoints)}", model.SeedEndpoints);

        var stun = model.StunServers.FirstOrDefault() ?? string.Empty;
        model.StunServers = stun.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .ToList();
        await _settingsService.SetAsync(ModuleId, $"{nameof(P2PSettings)}:{nameof(model.StunServers)}", model.StunServers);

        //await _settingsService.SetAsync(ModuleId, $"{nameof(P2PSettings)}:{nameof(model.StunServers)}", model.StunServers);
        await _settingsService.SetAsync(ModuleId, $"{nameof(P2PSettings)}:{nameof(model.KeepAlivePeriodMs)}", model.KeepAlivePeriodMs);
        await _settingsService.SetAsync(ModuleId, $"{nameof(P2PSettings)}:{nameof(model.ActiveNodeSecondsBackupPeriod)}", model.ActiveNodeSecondsBackupPeriod);
        await _settingsService.SetAsync(ModuleId, $"{nameof(P2PSettings)}:{nameof(model.TopologyMaintenannceIntervalMs)}", model.TopologyMaintenannceIntervalMs);
        await _settingsService.SetAsync(ModuleId, $"{nameof(P2PSettings)}:{nameof(model.HttpTimeoutSeconds)}", model.HttpTimeoutSeconds);
        await _settingsService.SetAsync(ModuleId, $"{nameof(P2PSettings)}:{nameof(model.DefaultHandshakeTimeoutSeconds)}", model.DefaultHandshakeTimeoutSeconds);
        await _settingsService.SetAsync(ModuleId, $"{nameof(P2PSettings)}:{nameof(model.MaxRelayInactivitySeconds)}", model.MaxRelayInactivitySeconds);
        await _settingsService.SetAsync(ModuleId, $"{nameof(P2PSettings)}:{nameof(model.DefaultRequestTimeoutSeconds)}", model.DefaultRequestTimeoutSeconds);
        await _settingsService.SetAsync(ModuleId, $"{nameof(P2PSettings)}:{nameof(model.TimeSyncIntervalSeconds)}", model.TimeSyncIntervalSeconds);
    }

    private async Task ProcessSaveStorage(StorageSettings model)
    {
        if (model == null) return;
        await _settingsService.SetAsync(ModuleId, $"{nameof(StorageSettings)}:{nameof(model.PersistTimePeriodSeconds)}", model.PersistTimePeriodSeconds);
        await _settingsService.SetAsync(ModuleId, $"{nameof(StorageSettings)}:{nameof(model.MaxNewNodesDrip)}", model.MaxNewNodesDrip);
        await _settingsService.SetAsync(ModuleId, $"{nameof(StorageSettings)}:{nameof(model.RetainedHistoryEpochsCount)}", model.RetainedHistoryEpochsCount);
        await _settingsService.SetAsync(ModuleId, $"{nameof(StorageSettings)}:{nameof(model.RetainedNodeHistoryEpochs)}", model.RetainedNodeHistoryEpochs);
    }

    private async Task ProcessSaveGossip(GossipSettings model)
    {
        if (model == null) return;
        await _settingsService.SetAsync(ModuleId, $"{nameof(GossipSettings)}:{nameof(model.MaxNewNodesDrip)}", model.MaxNewNodesDrip);
        await _settingsService.SetAsync(ModuleId, $"{nameof(GossipSettings)}:{nameof(model.NodeDripPenalty)}", model.NodeDripPenalty);
        await _settingsService.SetAsync(ModuleId, $"{nameof(GossipSettings)}:{nameof(model.GossipNodesSeconds)}", model.GossipNodesSeconds);
        await _settingsService.SetAsync(ModuleId, $"{nameof(GossipSettings)}:{nameof(model.FanoutTargetCount)}", model.FanoutTargetCount);
        await _settingsService.SetAsync(ModuleId, $"{nameof(GossipSettings)}:{nameof(model.GossipInBatches)}", model.GossipInBatches);
        await _settingsService.SetAsync(ModuleId, $"{nameof(GossipSettings)}:{nameof(model.GossipStaleAgeMs)}", model.GossipStaleAgeMs);
        await _settingsService.SetAsync(ModuleId, $"{nameof(GossipSettings)}:{nameof(model.SeenReportCacheTtlMinutes)}", model.SeenReportCacheTtlMinutes);
    }

    private async Task ProcessSaveRating(RatingSettings model)
    {
        if (model == null) return;
        await _settingsService.SetAsync(ModuleId, $"{nameof(RatingSettings)}:{nameof(model.HandshakeRepDelta)}", model.HandshakeRepDelta);
        await _settingsService.SetAsync(ModuleId, $"{nameof(RatingSettings)}:{nameof(model.SenioritySmoothingCoefficient)}", model.SenioritySmoothingCoefficient);
        await _settingsService.SetAsync(ModuleId, $"{nameof(RatingSettings)}:{nameof(model.MinimumTrustThreshold)}", model.MinimumTrustThreshold);
        await _settingsService.SetAsync(ModuleId, $"{nameof(RatingSettings)}:{nameof(model.PositiveTrustDecayRate)}", model.PositiveTrustDecayRate);
        await _settingsService.SetAsync(ModuleId, $"{nameof(RatingSettings)}:{nameof(model.NegativeTrustIndulgenceRate)}", model.NegativeTrustIndulgenceRate);
        await _settingsService.SetAsync(ModuleId, $"{nameof(RatingSettings)}:{nameof(model.ConsensusSelfTrust)}", model.ConsensusSelfTrust);
        await _settingsService.SetAsync(ModuleId, $"{nameof(RatingSettings)}:{nameof(model.ConsensusOutOfTImeStartThreshold)}", model.ConsensusOutOfTImeStartThreshold);
        await _settingsService.SetAsync(ModuleId, $"{nameof(RatingSettings)}:{nameof(model.ConsensusOutOfTImePrice)}", model.ConsensusOutOfTImePrice);
    }

    #endregion
}