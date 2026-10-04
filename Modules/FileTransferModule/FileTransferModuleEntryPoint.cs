using Common.Interfaces.Consensus;
using Common.Interfaces.Menu;
using Common.Models;

using FileTransferModule.Configuration;
using FileTransferModule.Services;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

using ProcessorApplication.Infrastructure;
using ProcessorApplication.Utils;

namespace FileTransferModule;


/// <summary>
/// Main entry point for the PTorrent File Sharing & Transfer Module.
/// Integrates storage engines, swarm coordinators, catalog crawlers, admin menus, and routing endpoints.
/// </summary>

/// <summary>
/// Main entry point for the PTorrent File Sharing & Transfer Module.
/// Integrates storage engines, swarm coordinators, catalog crawlers, admin menus, and routing endpoints.
/// </summary>
public class FileTransferModuleEntryPoint : IModule
{
    public const string MODULE_ID = "FileTransfer";
    public const string MODULE_NAMESPACE = "FileTransferModule";
    public string Name => "PTorrent";
    public string ModuleId => MODULE_ID;
    public Version Version => new Version("1.0.0.0");

    // Extensible roles for the informational system.
    // "file_tracker" (opt-in via P2PSettings.NodeRoles, gossiped) = this node runs the tracker/
    // directory service (FileTrackerCoordinator answers TrackerAnnounce/TrackerGetPeers/etc) and
    // also participates in file exchange itself.
    // "file_node" (opt-in via P2PSettings.NodeRoles, gossiped) = this node participates in file
    // exchange (can seed/download) without necessarily running the tracker service. Both are
    // independent opt-ins - a tracker that also seeds should have both set. Outbound
    // TrackerAnnounce (see FileTrackerAnnounceService) only fires when this node itself
    // advertises "file_node".
    public const string FileTracker = "file_tracker";
    public const string FileNode = "file_node";
    public IEnumerable<string> ListAdvertisedCapabilities() => new[] { FileTracker, FileNode };

    public ModuleDependency[] Dependencies => new[]
    {
        new ModuleDependency { ModuleId = "P2P", MinVersion = new Version("1.0.0.0") }
    };

    public IEnumerable<string> GetDefinedRoles() => Array.Empty<string>();
    public IEnumerable<string> GetRequiredRoles() => new[] { "Admin" };

    public FileTransferModuleEntryPoint()
    {
    }

    public IEnumerable<MenuItemViewModel> GetMenuItems(IServiceProvider services)
    {
        return new[]
        {
            new MenuItemViewModel
            {
                Name = "Hosted Archive",
                IconClass = "fa-solid fa-box-archive",
                Url = $"/{ModuleId}/FileArchive/Index",
                Roles = "Admin"
            },
            new MenuItemViewModel
            {
                Name = "Swarm Catalog",
                IconClass = "fa-solid fa-list-check",
                Url = $"/{ModuleId}/FileCatalog/Index",
                Roles = "Admin"
            },
            new MenuItemViewModel
            {
                Name = "Active Transfers",
                IconClass = "fa-solid fa-arrow-right-arrow-left",
                Url = $"/{ModuleId}/FileExchange/Index",
                Roles = "Admin"
            },
            new MenuItemViewModel
            {
                Name = "Settings",
                IconClass = "fa-solid fa-sliders",
                Url = $"/{ModuleId}/Settings/Index",
                Roles = "Admin"
            }
        };
    }

    public void ConfigureServices(IServiceCollection services, IConfiguration config)
    {
        // 1. Bind Options Pattern for module settings
        services.AddModuleSettings<FileSharingSettings>(config, ModuleId);

        // 2. Register low-level binary wire protocol & slice staging/storage engine
        services.AddSingleton<ITorrentWireHandler, TorrentWireHandler>();
        services.AddSingleton<IFileChunkStorageEngine, FileChunkStorageEngine>();

        // 3. Register Swarm orchestrator, multi-source download coordinator & tracker role handler
        services.AddSingleton<IFileTrackerCoordinator, FileTrackerCoordinator>();
        services.AddSingleton<ISwarmOrchestratorService, SwarmOrchestratorService>();
        services.AddSingleton<ISwarmDownloadCoordinator, SwarmDownloadCoordinator>();

        // 4. Register local hosted archive and network catalog services
        services.AddSingleton<IFileArchiveService, FileArchiveService>();

        // 5. File catalog consensus channel - implements Common.Interfaces.Consensus.IConsensusChannel
        // so it's picked up automatically by P2PModule's ConsensusChannelRegistry. Advertise it by
        // adding "filetr.catalog" (FileCatalogConsensusChannel.CHANNEL_ID) to P2PSettings'
        // AdvertisedConsensusChannels - nothing is advertised/synced until that's done.
        services.AddSingleton<FileCatalogConsensusChannel>();
        services.AddSingleton<IConsensusChannel>(sp => sp.GetRequiredService<FileCatalogConsensusChannel>());
        services.AddSingleton<IFileCatalogConsensusChannel>(sp => sp.GetRequiredService<FileCatalogConsensusChannel>());
        // Runs the periodic flush of batched "still present" refreshes and the stale-record sweep.
        services.AddHostedService(sp => sp.GetRequiredService<FileCatalogConsensusChannel>());

        services.AddSingleton<IFileCatalogService, FileCatalogService>();

        // 6. Outbound TrackerAnnounce - see FileTrackerAnnounceService remarks. Only announces
        // when this node advertises "file_node" in P2PSettings.NodeRoles.
        services.AddSingleton<FileTrackerAnnounceService>();
        services.AddSingleton<IFileTrackerAnnounceService>(sp => sp.GetRequiredService<FileTrackerAnnounceService>());
        services.AddHostedService(sp => sp.GetRequiredService<FileTrackerAnnounceService>());
    }

    public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
    {
        var assembly = typeof(FileTransferModuleEntryPoint).Assembly;
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
    }

    public IEnumerable<IConfigurationSource> GetConfigurationSources(IConfiguration initialConfig)
    {
        // Settings are saved and dynamically synchronized via main database / ISettingService
        yield break;
    }

    public void ConfigureEndpoints(IEndpointRouteBuilder endpoints)
    {
    }
}

/*
/// <summary>
/// Main entry point for the Tor-like decentralized Kestrel Mesh P2P Module.
/// Integrates services, exposes admin menus, and hard-wires routing endpoints.
/// </summary>

public class FileTrModuleEntryPoint : IModule
{
    public const string MODULE_ID = "FileTr";
    public const string MODULE_NAMESPACE = "FileTransferModule";
    public string Name => "PTorrent";
    public string ModuleId => MODULE_ID;
    public Version Version => new Version("1.0.0.0");

    // Extensible roles for the informational system
    public const string FileTracker = "file_tracker";
    public IEnumerable<string> ListAdvertisedCapabilities() => new[] { FileTracker };

    public ModuleDependency[] Dependencies => new[]
    {
        new ModuleDependency { ModuleId = "P2P", MinVersion = new Version("1.0.0.0") }
    };

    public FileTrModuleEntryPoint()
    {
    }

    public IEnumerable<string> GetDefinedRoles() => Array.Empty<string>();
    public IEnumerable<string> GetRequiredRoles() => new[] { "Admin" };

    public List<MenuItemViewModel> GetMenuItems(IServiceProvider services)
    {
        //var menu = new List<MenuItemViewModel>();
        return new List<MenuItemViewModel>
        {
            //exchange stuff
            new MenuItemViewModel {
                Name = "Exchanges",
                IconClass = "fa-solid fa-chart-pie",
                Url = "/FileTr/FileExchange/Index",
                Roles = "Admin"
            },
            //stuff stored, unordered, by default sorted by date
            new MenuItemViewModel {
                Name = "Files Stored",
                IconClass = "fa-solid fa-circle-nodes",
                Url = "/FileTr/FileArchive/Index",
                Roles = "Admin"
            },
            //find stuff to download, query nodes, drop .ptorrent here
            new MenuItemViewModel {
                Name = "Catalog",
                IconClass = "fa-solid fa-sliders",
                Url = "/FileTr/FileCatalog/Index",
                Roles = "Admin"
            },
            new MenuItemViewModel {
                Name = "Transfer Settings",
                IconClass = "fa-solid fa-gear",
                Url = "/FileTr/Settings/Index",
                Roles = "Admin"
            }
        };
    }
    public void ConfigureServices(IServiceCollection services, IConfiguration config)
    {
        // 1. Bind Options Patterns for module settings
        services.AddModuleSettings<FileSharingSettings>(config, ModuleId);
    }

    public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
    {
        var assembly = typeof(FileTrModuleEntryPoint).Assembly;
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
        //using var scope = host.Services.CreateScope();

        //// Cleaned up the underscore prefixes to point to standard, structured names
        //var identity = scope.ServiceProvider.GetRequiredService<INodeIdentityService>();
        //var overrides = scope.ServiceProvider.GetRequiredService<IPeerRatingOverrideService>();

        //// Initialize keys and ID from local storage, or derive a new key pair asynchronously
        //identity.LoadOrCreateIdentity();

        //// Restore manual administrative override tables completely offline
        //overrides.LoadRulesAsync().Wait();
    }

    public IEnumerable<IConfigurationSource> GetConfigurationSources(IConfiguration initialConfig)
    {
        // settings are saved in main database
        // this is here in case module has its own settings sources and mechanisms
        yield break;
    }

    public void ConfigureEndpoints(IEndpointRouteBuilder endpoints)
    {
    }
}
*/