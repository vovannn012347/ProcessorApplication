using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

using ProcessorApplication.Attributes;

using ProviderlessModule.Configuration;
using ProviderlessModule.Infrastructure.Interfaces;
using ProviderlessModule.Models;
using ProviderlessModule.Services;


namespace ProviderlessModule.Controllers;

[Authorize(Roles = "Admin")]
[Route("")]
[Route("Providerless")]
[ModuleRoute("Providerless")]
[Route("/[area]/[controller]/[action]/{id?}")]
public class HomeController : Controller
{
    //protected ClaimsPrincipal CurrentUser => User;
    private readonly ITunnelSelector _selector;
    private readonly IOptionsMonitor<PortalAccessSettings> _general;
    private readonly IRegistrySelector _registrySelector;
    private readonly PortalState _state;
    private readonly IPortalControlSignal _controlSignal;

    public HomeController(
        ITunnelSelector selector,
        IRegistrySelector registrySelector,
        PortalState state,
        IOptionsMonitor<PortalAccessSettings> general,
        IPortalControlSignal controlSignal)
    {
        _selector = selector;
        _general = general;
        _registrySelector = registrySelector;
        _state = state;
        _controlSignal = controlSignal;
    }

    [HttpGet("")]
    [HttpGet("Index")]
    public IActionResult Index() => View();



    [HttpGet]
    public IActionResult GetStatus()
    {
        var provider = _selector.GetActiveProvider();
        var registry = _registrySelector.GetActiveRegistry();
        var general = _general.CurrentValue;

        // Only real addresses (the token tunnel reports a placeholder when it has no public hostname)
        string? tunnelUrl = provider.IsRunning && IsHttpUrl(provider.CurrentUrl) ? provider.CurrentUrl : null;

        bool registryUsed = general.RegistryEnabled && registry.Provider != RegistryProviderType.None;
        string? publicUrl = registryUsed && registry.IsActive && tunnelUrl != null ? registry.GetQrDiscoveryUrl() : null;
        if (string.IsNullOrEmpty(publicUrl)) publicUrl = null;

        return Json(new QrStatusResult
        {
            TunnelActive = provider.IsRunning,
            RegistyActive = registryUsed && registry.IsActive,
            RegistryEnabled = registryUsed,
            Url = publicUrl ?? tunnelUrl ?? string.Empty,
            TunnelUrl = tunnelUrl,
            PublicUrl = publicUrl,
            RegisteredUrl = registryUsed ? registry.RegisteredUrl : null,
            RegistryEntryUrl = registryUsed ? registry.GetRegistryEntryUrl() : null,
            StartTime = provider.LastStartTime?.ToString("HH:mm:ss") ?? "--:--",
            ProviderName = provider.Provider.ToString(),
            LastError = _state.LastError ?? string.Empty
        });
    }

    private static bool IsHttpUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);

    [HttpPost]
    [Authorize(Policy = "AdminLocalPolicy")]
    public async Task<IActionResult> Reestablish()
    {
        var provider = _selector.GetActiveProvider();
        await provider.StopTunnelAsync();

        // 1. Mark the state so the orchestrator knows to kill the current process
        _state.RestartRequested = true;
        _state.LastError = null; // Clear old errors for a fresh start

        // 2. Poke the orchestrator to wake up immediately
        _controlSignal.RequestRestart();

        // In a real scenario, you'd get the port from IServerAddressesFeature 
        // as discussed, but for this button, we force a restart.
        return Json(new { success = true });
    }
}