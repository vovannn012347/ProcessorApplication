using System.Collections.Concurrent;

using FileTransferModule.Models;
using FileTransferModule.Models.Torrent;
using FileTransferModule.Services;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using ProcessingModule.Configuration;
using ProcessingModule.Database;
using ProcessingModule.Services.Installation;

using ScriptProvisioning.Configuration;
using ScriptProvisioning.Models;

namespace ScriptProvisioning.Services;

public interface IScriptAcquisitionService
{
    /// <summary>Script packages known to the local file catalogue (no network calls), with their status on this node.</summary>
    Task<List<RemoteScriptPackage>> GetRemotePackagesAsync(CancellationToken ct = default);

    /// <summary>Asks one node directly for its file list (all pages) and returns how many script packages it offers.</summary>
    Task<int> ProbeNodeAsync(string nodeId, CancellationToken ct = default);

    /// <summary>
    /// Starts downloading and installing a package in the background. <paramref name="replace"/> replaces an installed
    /// build with the same script ID and version (only after the admin saw the conflict).
    /// Returns the task, or an existing running task for the same package.
    /// </summary>
    Task<InstallTask> StartInstallAsync(string contentHash, bool replace, CancellationToken ct = default);

    /// <summary>Running and recently finished install tasks, newest first.</summary>
    List<InstallTask> GetTasks();
}

/// <summary>
/// Gets script packages from other nodes: finds them in the FileTransferModule catalogue (by file name, see
/// <see cref="ScriptPackageName"/>), downloads them with the swarm downloader (block hashes verified there), and
/// installs them with ProcessingModule's installer - the same validation as a local upload. The package must
/// contain the build its name describes; same script+version with different files is never replaced silently.
/// </summary>
public sealed class ScriptAcquisitionService : IScriptAcquisitionService
{
    private const int CatalogPageSize = 200;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    private readonly IServiceScopeFactory _scopes;
    private readonly IFileCatalogService _catalog;
    private readonly ISwarmDownloadCoordinator _downloads;
    private readonly IFileArchiveService _archive;
    private readonly IProvisioningStore _store;
    private readonly IOptionsMonitor<ScriptProvisioningSettings> _settings;
    private readonly IOptionsMonitor<ProcessorSettings> _processorSettings;
    private readonly ILogger<ScriptAcquisitionService> _logger;

    private readonly ConcurrentDictionary<string, InstallTask> _tasks = new();
    // One install at a time: downloads may overlap in FileTransferModule, but packages are installed one by one
    private readonly SemaphoreSlim _installLock = new(1, 1);

    public ScriptAcquisitionService(IServiceScopeFactory scopes, IFileCatalogService catalog, ISwarmDownloadCoordinator downloads,
        IFileArchiveService archive, IProvisioningStore store, IOptionsMonitor<ScriptProvisioningSettings> settings,
        IOptionsMonitor<ProcessorSettings> processorSettings, ILogger<ScriptAcquisitionService> logger)
    {
        _scopes = scopes;
        _catalog = catalog;
        _downloads = downloads;
        _archive = archive;
        _store = store;
        _settings = settings;
        _processorSettings = processorSettings;
        _logger = logger;
    }

    // ------------------------------------------------------------------ catalogue

    private async Task<List<FileCatalogEntry>> GetCatalogEntriesAsync()
    {
        var all = new List<FileCatalogEntry>();
        for (int page = 0; page < 1000; page++)
        {
            var response = await _catalog.GetLocalCatalogPagedAsync(page, CatalogPageSize, includeHidden: false,
                hostNodeIdFilter: null, fileNameSearch: ScriptPackageName.Suffix);
            all.AddRange(response.Entries);
            if (!response.HasMore || response.Entries.Count == 0) break;
        }
        return all;
    }

    public async Task<List<RemoteScriptPackage>> GetRemotePackagesAsync(CancellationToken ct = default)
    {
        var entries = await GetCatalogEntriesAsync();

        List<(int Id, string ScriptId, string? Version, string? Hash)> builds;
        using (var scope = _scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ProcessorDbContext>();
            builds = (await db.Scripts.AsNoTracking()
                    .Select(s => new { s.Id, s.ScriptIdentifier, s.ScriptVersion, s.ArtifactHash })
                    .ToListAsync(ct))
                .Select(s => (s.Id, s.ScriptIdentifier, (string?)s.ScriptVersion, (string?)s.ArtifactHash))
                .ToList();
        }
        var publishedHashes = new HashSet<string>(_store.GetPublished().Select(p => p.ContentHash), StringComparer.OrdinalIgnoreCase);
        var activeTasks = _tasks.Values.Where(t => !t.IsFinished).ToDictionary(t => t.ContentHash, t => t.Id, StringComparer.OrdinalIgnoreCase);

        var result = new List<RemoteScriptPackage>();
        foreach (var group in entries.Where(e => !string.IsNullOrEmpty(e.Manifest.ContentHash))
                     .GroupBy(e => e.Manifest.ContentHash, StringComparer.OrdinalIgnoreCase))
        {
            var first = group.First();
            var name = ScriptPackageName.TryParse(first.Manifest.FileName);
            if (name == null) continue;

            var item = new RemoteScriptPackage
            {
                ContentHash = group.Key.ToLowerInvariant(),
                FileName = first.Manifest.FileName,
                ScriptId = name.ScriptId,
                Version = name.Version,
                HashPrefix = name.HashPrefix,
                SizeBytes = first.Manifest.TotalSizeBytes,
                HostNodeIds = group.Where(e => !e.IsLocalSeeding).Select(e => e.HostNodeId).Where(h => !string.IsNullOrEmpty(h)).Distinct().ToList(),
                PublishedHere = publishedHashes.Contains(group.Key) || group.Any(e => e.IsLocalSeeding),
                LastSeenUtc = group.Max(e => e.DiscoveredUtc),
                ActiveTaskId = activeTasks.GetValueOrDefault(group.Key)
            };

            var sameScript = builds.Where(b => b.ScriptId == name.ScriptId).ToList();
            var sameVersion = sameScript.Where(b => ScriptPackageName.SafeVersion(b.Version) == name.Version).ToList();
            var exact = sameVersion.FirstOrDefault(b => name.Describes(b.ScriptId, b.Version, b.Hash));
            if (exact != default)
            {
                item.Status = RemotePackageStatus.Installed;
                item.LocalBuildId = exact.Id;
                item.LocalArtifactHash = exact.Hash;
            }
            else if (sameVersion.Count > 0)
            {
                item.Status = RemotePackageStatus.Conflict;
                item.LocalBuildId = sameVersion[0].Id;
                item.LocalArtifactHash = sameVersion[0].Hash;
            }
            else
            {
                item.Status = sameScript.Count > 0 ? RemotePackageStatus.NewVersion : RemotePackageStatus.New;
            }
            result.Add(item);
        }

        return result
            .OrderBy(r => r.ScriptId, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(r => r.Version, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<int> ProbeNodeAsync(string nodeId, CancellationToken ct = default)
    {
        int found = 0;
        for (int page = 0; page < 1000; page++)
        {
            ct.ThrowIfCancellationRequested();
            var response = await _catalog.QueryNodeCatalogPagedAsync(nodeId, page, CatalogPageSize);
            found += response.Entries.Count(e => ScriptPackageName.TryParse(e.Manifest.FileName) != null);
            if (!response.HasMore || response.Entries.Count == 0) break;
        }
        return found;
    }

    // ------------------------------------------------------------------ install

    public List<InstallTask> GetTasks()
    {
        Trim();
        return _tasks.Values.OrderByDescending(t => t.StartedUtc).ToList();
    }

    private void Trim()
    {
        int keep = Math.Max(5, _settings.CurrentValue.KeepFinishedTasks);
        foreach (var old in _tasks.Values.Where(t => t.IsFinished).OrderByDescending(t => t.FinishedUtc).Skip(keep).ToList())
            _tasks.TryRemove(old.Id, out _);
    }

    public async Task<InstallTask> StartInstallAsync(string contentHash, bool replace, CancellationToken ct = default)
    {
        contentHash = (contentHash ?? "").Trim().ToLowerInvariant();

        var running = _tasks.Values.FirstOrDefault(t => !t.IsFinished && t.ContentHash == contentHash);
        if (running != null) return running;

        var task = new InstallTask { ContentHash = contentHash, Replace = replace };
        var package = (await GetRemotePackagesAsync(ct)).FirstOrDefault(p => p.ContentHash == contentHash);
        if (package == null)
        {
            Finish(task, InstallTaskState.Failed, "This package isn't in the file catalogue (any more). Refresh the list or probe its node.");
            _tasks[task.Id] = task;
            return task;
        }

        task.FileName = package.FileName;
        task.HostNodeIds = package.HostNodeIds;
        task.ScriptId = package.ScriptId;
        task.Version = package.Version;
        task.Message = "Waiting for the previous install to finish.";
        _tasks[task.Id] = task;

        _ = Task.Run(() => RunAsync(task));
        return task;
    }

    private async Task RunAsync(InstallTask task)
    {
        await _installLock.WaitAsync();
        try
        {
            await RunCoreAsync(task);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ScriptProvisioning] Installing {File} failed", task.FileName);
            Finish(task, InstallTaskState.Failed, "Unexpected error: " + ex.Message);
        }
        finally
        {
            _installLock.Release();
        }
    }

    private async Task RunCoreAsync(InstallTask task)
    {
        var name = ScriptPackageName.TryParse(task.FileName);
        if (name == null)
        {
            Finish(task, InstallTaskState.Failed, $"'{task.FileName}' is not a script package name.");
            return;
        }

        long maxBytes = (long)Math.Max(1, _processorSettings.CurrentValue.MaxScriptUploadMb) * 1024 * 1024;

        // 1. A copy we already have (our own package, or downloaded before and still seeded): no download
        string? path = null;
        var tracked = await _archive.GetTrackedFileAsync(task.ContentHash);
        if (tracked != null && File.Exists(tracked.LocalFilePath) && new FileInfo(tracked.LocalFilePath).Length == tracked.Manifest.TotalSizeBytes)
        {
            path = tracked.LocalFilePath;
        }
        else
        {
            // 2. Manifest (block hashes) from one of the hosts
            Set(task, InstallTaskState.FetchingManifest, "Asking the host for the package manifest.");
            PTorrentManifest? manifest = null;
            foreach (var host in task.HostNodeIds)
            {
                try
                {
                    manifest = await _catalog.FetchManifestFromNodeAsync(host, task.ContentHash);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[ScriptProvisioning] Manifest request to {Host} failed", host);
                }
                if (manifest != null) break;
            }
            if (manifest == null)
            {
                Finish(task, InstallTaskState.Failed, task.HostNodeIds.Count == 0
                    ? "No other node is known to offer this package."
                    : "None of the hosts returned the package manifest (offline, or no longer offering it).");
                return;
            }
            if (!string.Equals(manifest.ContentHash, task.ContentHash, StringComparison.OrdinalIgnoreCase) || !manifest.VerifyContentHash())
            {
                Finish(task, InstallTaskState.Failed, "The host returned a manifest that doesn't match the package.");
                return;
            }
            if (!string.Equals(manifest.FileName, task.FileName, StringComparison.Ordinal))
            {
                Finish(task, InstallTaskState.Failed, $"The manifest names the file '{manifest.FileName}', the catalogue said '{task.FileName}'.");
                return;
            }
            if (manifest.TotalSizeBytes > maxBytes)
            {
                Finish(task, InstallTaskState.Failed, $"The package is {manifest.TotalSizeBytes / 1048576.0:F1} MB; script uploads are limited to {_processorSettings.CurrentValue.MaxScriptUploadMb} MB (Processing settings).");
                return;
            }

            // 3. Download (FileTransferModule checks every block against the manifest)
            path = await DownloadAsync(task, manifest);
            if (path == null) return; // task already finished with the reason
        }

        // 4. Stage with the normal installer and check that the package is what its name says
        Set(task, InstallTaskState.Verifying, "Checking the package.");
        using var scope = _scopes.CreateScope();
        var installer = scope.ServiceProvider.GetRequiredService<IScriptInstaller>();
        var db = scope.ServiceProvider.GetRequiredService<ProcessorDbContext>();

        ScriptInstallResult staged;
        string source = $"{task.FileName} from node {string.Join(", ", task.HostNodeIds.DefaultIfEmpty("(local copy)"))}";
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            staged = await installer.StageAsync(stream, source);
        }

        bool committed = false;
        try
        {
            var report = staged.Report;
            task.ScriptId = report.ScriptId ?? task.ScriptId;
            task.Version = report.Version ?? task.Version;
            task.ArtifactHash = report.ComputedHash;
            task.Report = report.Items
                .Where(i => i.Level != ReportLevel.Info)
                .Select(i => $"{i.Level}: {i.Area}: {i.Message}")
                .Take(30).ToList();

            if (staged.Status == ScriptInstallStatus.Rejected || staged.StageId == null)
            {
                Finish(task, InstallTaskState.Failed, "The package failed validation: " + staged.Message);
                return;
            }
            if (report.ScriptId == null || !name.Describes(report.ScriptId, report.Version, report.ComputedHash))
            {
                Finish(task, InstallTaskState.Failed,
                    $"The package contains {report.ScriptId} {report.Version} (hash {Short(report.ComputedHash)}), not what its name says ({name.ScriptId} {name.Version}, hash {name.HashPrefix}). Not installed.");
                return;
            }

            bool overwrite = false;
            if (staged.Status == ScriptInstallStatus.NeedsConfirmation)
            {
                var installed = await db.Scripts.AsNoTracking()
                    .Where(s => s.ScriptIdentifier == report.ScriptId && s.ScriptVersion == report.Version)
                    .ToListAsync();
                task.InstalledArtifactHash = installed.Select(s => s.ArtifactHash).FirstOrDefault();

                var same = installed.FirstOrDefault(s => string.Equals(s.ArtifactHash, report.ComputedHash, StringComparison.OrdinalIgnoreCase));
                if (same != null)
                {
                    task.ScriptIndexId = same.Id;
                    Finish(task, InstallTaskState.AlreadyInstalled, $"{report.ScriptId} {report.Version} is already installed with the same files.");
                    return;
                }
                if (!task.Replace)
                {
                    Finish(task, InstallTaskState.Conflict,
                        $"{report.ScriptId} {report.Version} is already installed with different files (installed {Short(task.InstalledArtifactHash)}, package {Short(report.ComputedHash)}). Choose Replace to install the package instead.");
                    return;
                }
                overwrite = true;
            }

            Set(task, InstallTaskState.Installing, overwrite ? "Replacing the installed build." : "Installing.");
            var result = await installer.CommitAsync(staged.StageId, overwrite);
            if (result.Status != ScriptInstallStatus.Installed)
            {
                Finish(task, InstallTaskState.Failed, "Installing failed: " + result.Message);
                return;
            }
            committed = true;

            task.ScriptIndexId = result.ScriptIndexId;
            _store.AddInstallRecord(new InstallRecord
            {
                ContentHash = task.ContentHash,
                FileName = task.FileName,
                HostNodeIds = task.HostNodeIds,
                ScriptId = report.ScriptId,
                Version = report.Version ?? "",
                ArtifactHash = report.ComputedHash ?? "",
                ScriptIndexId = result.ScriptIndexId,
                InstalledFolder = result.InstalledFolder,
                Replaced = overwrite,
                InstalledUtc = DateTime.UtcNow
            });
            _logger.LogInformation("[ScriptProvisioning] Installed {Script} {Version} from {Hosts} ({Hash})",
                report.ScriptId, report.Version, string.Join(",", task.HostNodeIds), task.ContentHash);
            Finish(task, InstallTaskState.Installed, $"Installed {report.ScriptId} {report.Version} into {result.InstalledFolder}.");
        }
        finally
        {
            if (!committed && staged.StageId != null) installer.Discard(staged.StageId);
        }
    }

    private async Task<string?> DownloadAsync(InstallTask task, PTorrentManifest manifest)
    {
        Set(task, InstallTaskState.Downloading, "Downloading.");
        string downloadId = await _downloads.StartDownloadAsync(manifest, customTargetDirectory: null, customFileName: null, isLocalAdmin: false);
        task.DownloadTaskId = downloadId;

        var deadline = DateTime.UtcNow.AddMinutes(Math.Max(1, _settings.CurrentValue.DownloadTimeoutMinutes));
        while (true)
        {
            var state = _downloads.GetTaskState(downloadId);
            if (state == null)
            {
                Finish(task, InstallTaskState.Failed, "The download task disappeared (cancelled in Active Transfers?).");
                return null;
            }

            task.Progress = Math.Round(state.ProgressPercentage, 1);
            switch (state.Status)
            {
                case SwarmTaskStatus.Complete:
                    if (string.IsNullOrEmpty(state.TargetSavePath) || !File.Exists(state.TargetSavePath))
                    {
                        Finish(task, InstallTaskState.Failed, "The download finished but the file isn't there.");
                        return null;
                    }
                    if (new FileInfo(state.TargetSavePath).Length != manifest.TotalSizeBytes)
                    {
                        Finish(task, InstallTaskState.Failed, "The downloaded file has the wrong size.");
                        return null;
                    }
                    task.Progress = 100;
                    return state.TargetSavePath;

                case SwarmTaskStatus.Failed:
                    Finish(task, InstallTaskState.Failed, "Download failed: " + (string.IsNullOrEmpty(state.ErrorMessage) ? "no reason given" : state.ErrorMessage));
                    return null;

                case SwarmTaskStatus.Paused:
                    task.Message = "Download paused (resume it in Active Transfers).";
                    break;

                default:
                    task.Message = $"Downloading: {task.Progress:F1}% from {state.ActiveSeedersCount} seeder(s).";
                    break;
            }

            if (DateTime.UtcNow > deadline)
            {
                try { await _downloads.CancelDownloadAsync(downloadId); } catch { /* best effort */ }
                Finish(task, InstallTaskState.Failed, $"The download didn't finish within {_settings.CurrentValue.DownloadTimeoutMinutes} minutes.");
                return null;
            }
            await Task.Delay(PollInterval);
        }
    }

    private static void Set(InstallTask task, InstallTaskState state, string message)
    {
        task.State = state;
        task.Message = message;
    }

    private void Finish(InstallTask task, InstallTaskState state, string message)
    {
        task.State = state;
        task.Message = message;
        task.FinishedUtc = DateTime.UtcNow;
        if (state == InstallTaskState.Failed)
            _logger.LogWarning("[ScriptProvisioning] {File}: {Message}", task.FileName, message);
    }

    private static string Short(string? hash) => string.IsNullOrEmpty(hash) ? "(none)" : hash.Length > 12 ? hash[..12] : hash;
}
