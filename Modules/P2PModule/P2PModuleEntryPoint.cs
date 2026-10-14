using Common.Interfaces;
using Common.Interfaces.Menu;
using Common.Models;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

using P2PModule.Configuration;
using P2PModule.Infrastructure.Endpoints;
using P2PModule.Interfaces;
using P2PModule.Services.Connection;
using P2PModule.Services.Discovery;
using P2PModule.Services.Discovery.Gossip;
using P2PModule.Services.Gossip;
using P2PModule.Services.Maintenance;
using P2PModule.Services.Persistence;
using P2PModule.Services.Rating;

using Common.Interfaces.Consensus;

using ProcessorApplication.Infrastructure;
using ProcessorApplication.Infrastructure.Web;
using ProcessorApplication.Utils;

namespace P2PModule;

/// <summary>
/// Main entry point for the decentralized Kestrel Mesh P2P Module.
/// Integrates services, exposes admin menus, and hard-wires routing endpoints.
/// </summary>

public class P2PModuleEntryPoint : IModule
{
    public const string MODULE_ID = "P2P";
    public const string MODULE_NAMESPACE = "P2PModule";
    public string Name => "Mesh P2P";
    public string ModuleId => MODULE_ID;
    public Version Version => new Version("1.0.0.0");

    // Extensible roles for the informational system
    public const string RoleTracker = "tracker";
    //public const string TempTracker = "promote d_tracker"; //temporary role, if no trackers

    public IEnumerable<string> ListAdvertisedCapabilities() => new[] { RoleTracker };

    public ModuleDependency[] Dependencies => new[]
    {
        new ModuleDependency { ModuleId = "Main", MinVersion = new Version("1.0.0.0") }
    };

    // these are roles defined specifically for access to different functions or management of module

    public const string NetworkAdminRole = "NetworkAdmin";

    public P2PModuleEntryPoint()
    {
    }

    public IEnumerable<string> GetDefinedRoles() => new[] { NetworkAdminRole };
    public IEnumerable<string> GetRequiredRoles() => new[] { NetworkAdminRole };

    public IEnumerable<MenuItemViewModel> GetMenuItems(IServiceProvider services)
    {
        //var menu = new List<MenuItemViewModel>();
        return new List<MenuItemViewModel>
        {
            new MenuItemViewModel {
                Name = "Mesh Overview",
                IconClass = "fa-solid fa-circle-nodes",
                Url = "/P2P/MeshControl/Index",
                Roles = "NetworkAdmin"
            },
            new MenuItemViewModel {
                Name = "Mesh Control",
                IconClass = "fa-solid fa-sliders",
                Url = "/P2P/MeshRatingControl/Index",
                Roles = "NetworkAdmin"
            },
            //new MenuItemViewModel {
            //    Name = "p2p_manual_discovery",
            //    IconClass = "fa-solid fa-satellite-dish",
            //    Url = "/P2P/ManualDiscovery/Index",
            //    Roles = "NetworkAdmin"
            //},
            new MenuItemViewModel {
                Name = "History",
                IconClass = "fa-solid fa-chart-pie",
                Url = "/P2P/HistoryMonitor/Index",
                Roles = "NetworkAdmin"
            },
            new MenuItemViewModel {
                Name = "P2P Settings",
                IconClass = "fa-solid fa-gear",
                Url = "/P2P/Settings/Index",
                Roles = "Admin"
            }
        };
    }
    public void ConfigureServices(IServiceCollection services, IConfiguration config)
    {
        // 1. Bind Options Patterns for module settings
        services.AddModuleSettings<P2PSettings>(config, ModuleId);
        services.AddModuleSettings<RatingSettings>(config, ModuleId);
        services.AddModuleSettings<GossipSettings>(config, ModuleId);
        services.AddModuleSettings<StorageSettings>(config, ModuleId);

        services.TryAddHttpClient();

        // 2. Core Security & Identity Services
        services.AddSingleton<INodeIdentityService, NodeIdentityService>();
        services.AddSingleton<IPeerRatingOverrideService, PeerRatingOverrideService>();
        services.AddSingleton<IRatingService, RatingService>();
        services.AddSingleton<IChallengeService, DoubleBlindChallengeService>();

        // 3. Storage & Tracker State
        services.AddSingleton<IConsensusStore, ConsensusStore>();
        services.AddSingleton<IPeerTracker, PeerTracker>();

        // 4. Transport & Peer Connectivity
        services.AddSingleton<IPeerConnectionManager, PeerConnectionManager>();
        services.AddSingleton<IP2PRouterService, P2PRouterService>();

        // 5. Mesh Orchestration & Management
        services.AddSingleton<ManagementService>();
        services.AddSingleton<IMeshManagementService>(sp => sp.GetRequiredService<ManagementService>());
        services.AddHostedService(sp => sp.GetRequiredService<ManagementService>());
        services.AddHostedService<MeshOrchestratorService>();

        // 6. Gossip Network Event Handlers
        services.AddSingleton<IP2PGossipService, P2PGossipService>();
        services.AddHostedService<GossipSelfIdentityUpdatedHandler>();
        services.AddHostedService<GossipNodeUpdatesHandler>();

        // 7. Decoupled Modular Consensus Stack
        // Level 0: Multi-channel consensus registry. Any module (including this one, and future
        // FileTransferModule/ScriptProvisioning channels) registers its own IConsensusChannel via
        // services.AddSingleton<IConsensusChannel, TChannel>() in its own ConfigureServices - they
        // all land in this same shared IServiceCollection and get picked up here automatically.
        services.AddSingleton<IConsensusChannel, PeerTrustConsensusChannel>();
        services.AddSingleton<IConsensusChannelRegistry, ConsensusChannelRegistry>();

        // Level 1: Pure Mathematical & File Staging Engine (Transient)
        services.AddTransient<IConsensusMergeEngine, ConsensusMergeEngine>();

        // Level 2: Ephemeral Active Consensus Session Manager (Singleton Lock & State)
        services.AddSingleton<IConsensusSessionManager, ConsensusSessionManager>();

        // Level 3a: Dynamic Router Handler & Protocol Processing (HostedService Listener)
        services.AddSingleton<ConsensusProtocolHandler>();
        services.AddSingleton<IConsensusProtocolHandler>(sp => sp.GetRequiredService<ConsensusProtocolHandler>());
        services.AddHostedService(sp =>  sp.GetRequiredService<ConsensusProtocolHandler>());

        // Level 3b: Nightly Jitter Trigger & Scheduled Consensus Initiator (HostedService Watchdog)
        services.AddHostedService<ConsensusInitiatorHostedService>();

        // Level 3c: Generic per-channel sync for any IConsensusChannel other than peer-trust
        // (see GenericChannelSyncService remarks). Manual-trigger only, no automatic scheduling -
        // call IGenericChannelSyncService.TriggerSyncAsync explicitly (admin UI, a hosted scheduler
        // you wire up separately, or a test harness) when you want a channel to actually sync.
        services.AddSingleton<GenericChannelSyncService>();
        services.AddSingleton<IGenericChannelSyncService>(sp => sp.GetRequiredService<GenericChannelSyncService>());
        services.AddHostedService(sp => sp.GetRequiredService<GenericChannelSyncService>());
    }

    public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
    {
        var assembly = typeof(P2PModuleEntryPoint).Assembly;
        var moduleFolder = Path.GetDirectoryName(assembly.Location);
        var wwwrootPath = Path.Combine(moduleFolder!, "wwwroot");

        if (Directory.Exists(wwwrootPath))
        {
            app.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = new PhysicalFileProvider(wwwrootPath),
                RequestPath = $"/{ModuleId}"
            });
        }
    }

    public void PrestartInit(IHost host)
    {
        using var scope = host.Services.CreateScope();

        // Cleaned up the underscore prefixes to point to standard, structured names
        var identity = scope.ServiceProvider.GetRequiredService<INodeIdentityService>();
        var overrides = scope.ServiceProvider.GetRequiredService<IPeerRatingOverrideService>();

        // Initialize keys and ID from local storage, or derive a new key pair asynchronously
        identity.LoadOrCreateIdentity();

        // Restore manual administrative override tables completely offline
        overrides.LoadRulesAsync().Wait();
    }

    public IEnumerable<IConfigurationSource> GetConfigurationSources(IConfiguration initialConfig)
    {
        // settings are saved in main database
        // this is here in case module has its own settings sources and mechanisms
        yield break;
    }

    public void ConfigureEndpoints(IEndpointRouteBuilder endpoints)
    {
        // endpoint additions go here
        // below is for some other stuff but i think this goes here 
        // Hard-wired rooted paths for P2P traffic (Bypassing standard MVC / Razor engines)
        //ConsensusEndpoints.Map(endpoints);

        ConnectionEndpoints.Map(endpoints);
    }
}