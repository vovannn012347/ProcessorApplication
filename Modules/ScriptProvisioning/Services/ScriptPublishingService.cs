using FileTransferModule.Services;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using ProcessingModule.Configuration;
using ProcessingModule.Database;
using ProcessingModule.Services.Installation;

using ScriptProvisioning.Configuration;
using ScriptProvisioning.Models;

namespace ScriptProvisioning.Services;

public sealed class PublishResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public PublishedPackage? Package { get; set; }
}

public interface IScriptPublishingService
{
    /// <summary>All installed builds with their publish state and origin.</summary>
    Task<List<PublishableBuild>> GetBuildsAsync(CancellationToken ct = default);

    /// <summary>
    /// Packs a build, checks the package exactly as a receiving node would (same installer, same validation,
    /// same hash), and starts seeding it through FileTransferModule.
    /// </summary>
    Task<PublishResult> PublishAsync(int scriptIndexId, bool? hidden = null, CancellationToken ct = default);

    /// <summary>Stops seeding the package and deletes it (the installed build itself stays).</summary>
    Task<PublishResult> UnpublishAsync(int scriptIndexId, CancellationToken ct = default);
}

public sealed class ScriptPublishingService : IScriptPublishingService
{
    /// <summary>Custom field keys set on published packages (only visible to peers whose access rules allow "pscript.*").</summary>
    public const string FieldPrefix = "pscript.";

    // Packing and seeding one package at a time keeps disk use and CPU predictable on a weak PC
    private static readonly SemaphoreSlim PublishLock = new(1, 1);

    private readonly ProcessorDbContext _db;
    private readonly IScriptInstaller _installer;
    private readonly IFileArchiveService _archive;
    private readonly IProvisioningStore _store;
    private readonly IOptionsMonitor<ScriptProvisioningSettings> _settings;
    private readonly IOptionsMonitor<ProcessorSettings> _processorSettings;
    private readonly ILogger<ScriptPublishingService> _logger;

    public ScriptPublishingService(ProcessorDbContext db, IScriptInstaller installer, IFileArchiveService archive,
        IProvisioningStore store, IOptionsMonitor<ScriptProvisioningSettings> settings,
        IOptionsMonitor<ProcessorSettings> processorSettings, ILogger<ScriptPublishingService> logger)
    {
        _db = db;
        _installer = installer;
        _archive = archive;
        _store = store;
        _settings = settings;
        _processorSettings = processorSettings;
        _logger = logger;
    }

    /// <summary>ManifestDirectoryPath is stored relative to ScriptSourcePath (same resolution as the launchers).</summary>
    private string BuildFolder(string manifestDirectoryPath) =>
        Path.GetFullPath(Path.Combine(_processorSettings.CurrentValue.ScriptSourcePath, manifestDirectoryPath ?? ""));

    public async Task<List<PublishableBuild>> GetBuildsAsync(CancellationToken ct = default)
    {
        var builds = await _db.Scripts.AsNoTracking()
            .OrderBy(s => s.ScriptLabel).ThenBy(s => s.ScriptIdentifier).ThenByDescending(s => s.CreatedTime)
            .ToListAsync(ct);
        var published = _store.GetPublished().ToDictionary(p => p.ScriptIndexId);
        var origins = _store.GetInstallRecords()
            .Where(r => r.ScriptIndexId.HasValue)
            .GroupBy(r => r.ScriptIndexId!.Value)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.InstalledUtc).First());

        var result = new List<PublishableBuild>();
        foreach (var b in builds)
        {
            var item = new PublishableBuild
            {
                ScriptIndexId = b.Id,
                ScriptId = b.ScriptIdentifier,
                Version = b.ScriptVersion ?? "",
                Label = b.ScriptLabel,
                ArtifactHash = b.ArtifactHash ?? "",
                IsAvailable = b.IsAvailable,
                HashMatch = b.HashMatch,
                Blocker = BlockerOf(b.IsAvailable, b.HashMatch, b.ArtifactHash),
                Origin = origins.GetValueOrDefault(b.Id)
            };
            if (published.TryGetValue(b.Id, out var p))
            {
                item.Published = p;
                var tracked = await _archive.GetTrackedFileAsync(p.ContentHash);
                item.Seeding = tracked != null && tracked.AllowDownload && File.Exists(tracked.LocalFilePath);
            }
            result.Add(item);
        }
        return result;
    }

    private static string? BlockerOf(bool available, bool hashMatch, string? artifactHash)
    {
        if (!available) return "The build is unavailable (files missing). Reindex or reinstall it first.";
        if (!hashMatch) return "The files no longer match the build's hash. Reindex or reinstall it first.";
        if (string.IsNullOrEmpty(artifactHash) || artifactHash.Length < ScriptPackageName.HashPrefixLength) return "The build has no hash yet. Reindex it first.";
        return null;
    }

    public async Task<PublishResult> PublishAsync(int scriptIndexId, bool? hidden = null, CancellationToken ct = default)
    {
        await PublishLock.WaitAsync(ct);
        try
        {
            var build = await _db.Scripts.AsNoTracking().FirstOrDefaultAsync(s => s.Id == scriptIndexId, ct);
            if (build == null) return Fail("Build not found.");

            string? blocker = BlockerOf(build.IsAvailable, build.HashMatch, build.ArtifactHash);
            if (blocker != null) return Fail(blocker);

            string fileName = ScriptPackageName.Build(build.ScriptIdentifier, build.ScriptVersion, build.ArtifactHash);
            Directory.CreateDirectory(_store.PublishedDirectory);
            string packagePath = Path.Combine(_store.PublishedDirectory, fileName);
            string workPath = Path.Combine(_store.PublishedDirectory, $".{Guid.NewGuid():N}.zip");

            int fileCount;
            try
            {
                fileCount = ScriptPackageBuilder.Build(BuildFolder(build.ManifestDirectoryPath), workPath);

                // Check the package as the receiving node will: same validator and hash
                string? problem = await CheckPackageAsync(workPath, build.ScriptIdentifier, build.ScriptVersion, build.ArtifactHash, ct);
                if (problem != null) return Fail(problem);

                // Same bytes as the package already offered (usual on republish): keep it - it may be open for seeding
                if (!(File.Exists(packagePath) && SameContent(packagePath, workPath)))
                    File.Move(workPath, packagePath, overwrite: true);
            }
            finally
            {
                if (File.Exists(workPath)) File.Delete(workPath);
            }

            bool isHidden = hidden ?? _settings.CurrentValue.PublishHidden;
            var previous = _store.GetPublished().FirstOrDefault(p => p.ScriptIndexId == scriptIndexId);

            var record = await _archive.IngestAndTrackFileAsync(packagePath, isHidden, copyToRepository: false);
            string contentHash = record.Manifest.ContentHash;

            await _archive.UpdateRecordSettingsAsync(contentHash, isHidden: isHidden, allowDownload: true,
                description: $"Script package: {build.ScriptLabel} ({build.ScriptIdentifier} {build.ScriptVersion})",
                customFields: new Dictionary<string, string>
                {
                    [FieldPrefix + "format"] = "1",
                    [FieldPrefix + "script_id"] = build.ScriptIdentifier,
                    [FieldPrefix + "version"] = build.ScriptVersion ?? "",
                    [FieldPrefix + "artifact_hash"] = build.ArtifactHash,
                    [FieldPrefix + "label"] = build.ScriptLabel ?? ""
                });

            // An older package of this build (e.g. packed before a reinstall) is replaced, not kept seeding
            if (previous != null && previous.ContentHash != contentHash)
            {
                await _archive.DeleteTrackedFileAsync(previous.ContentHash, deletePhysicalFile: false);
                if (!string.Equals(previous.FilePath, packagePath, StringComparison.OrdinalIgnoreCase)) TryDelete(previous.FilePath);
            }

            var package = new PublishedPackage
            {
                ScriptIndexId = build.Id,
                ScriptId = build.ScriptIdentifier,
                Version = build.ScriptVersion ?? "",
                Label = build.ScriptLabel,
                ArtifactHash = build.ArtifactHash,
                FileName = fileName,
                FilePath = packagePath,
                ContentHash = contentHash,
                SizeBytes = new FileInfo(packagePath).Length,
                FileCount = fileCount,
                Hidden = isHidden,
                PublishedUtc = DateTime.UtcNow
            };
            _store.SavePublished(package);

            _logger.LogInformation("[ScriptProvisioning] Published {Script} {Version} as {File} ({Hash})",
                build.ScriptIdentifier, build.ScriptVersion, fileName, contentHash);
            return new PublishResult { Success = true, Message = $"Published as {fileName}.", Package = package };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "[ScriptProvisioning] Publishing build {Id} failed", scriptIndexId);
            return Fail("Publishing failed: " + ex.Message);
        }
        finally
        {
            PublishLock.Release();
        }
    }

    /// <summary>Returns a problem description, or null if the package installs as this exact build.</summary>
    private async Task<string?> CheckPackageAsync(string packagePath, string scriptId, string? version, string artifactHash, CancellationToken ct)
    {
        ScriptInstallResult staged;
        using (var stream = File.OpenRead(packagePath))
        {
            staged = await _installer.StageAsync(stream, Path.GetFileName(packagePath), ct);
        }
        try
        {
            var report = staged.Report;
            if (staged.Status == ScriptInstallStatus.Rejected || report.HasErrors)
            {
                var errors = report.Items.Where(i => i.Level == ReportLevel.Error).Select(i => $"{i.Area}: {i.Message}").Take(5);
                return "The packed build doesn't pass validation: " + (staged.Message + " " + string.Join("; ", errors)).Trim();
            }
            if (report.ScriptId != scriptId || report.Version != version)
                return $"The packed build reads as {report.ScriptId} {report.Version}, expected {scriptId} {version}.";
            if (!string.Equals(report.ComputedHash, artifactHash, StringComparison.OrdinalIgnoreCase))
                return $"The packed build hashes to {Short(report.ComputedHash)}, but the library has {Short(artifactHash)}. Reindex the build first.";
            return null;
        }
        finally
        {
            if (staged.StageId != null) _installer.Discard(staged.StageId);
        }
    }

    public async Task<PublishResult> UnpublishAsync(int scriptIndexId, CancellationToken ct = default)
    {
        await PublishLock.WaitAsync(ct);
        try
        {
            var package = _store.GetPublished().FirstOrDefault(p => p.ScriptIndexId == scriptIndexId);
            if (package == null) return Fail("This build isn't published.");

            await _archive.DeleteTrackedFileAsync(package.ContentHash, deletePhysicalFile: false);
            TryDelete(package.FilePath);
            _store.RemovePublished(scriptIndexId);

            _logger.LogInformation("[ScriptProvisioning] Unpublished {Script} {Version} ({Hash})", package.ScriptId, package.Version, package.ContentHash);
            return new PublishResult { Success = true, Message = $"{package.FileName} is no longer offered.", Package = package };
        }
        finally
        {
            PublishLock.Release();
        }
    }

    private static bool SameContent(string a, string b)
    {
        if (new FileInfo(a).Length != new FileInfo(b).Length) return false;
        using var sha = System.Security.Cryptography.SHA256.Create();
        byte[] ha, hb;
        using (var fa = new FileStream(a, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) ha = sha.ComputeHash(fa);
        using (var fb = File.OpenRead(b)) hb = sha.ComputeHash(fb);
        return ha.AsSpan().SequenceEqual(hb);
    }

    private void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { _logger.LogWarning(ex, "[ScriptProvisioning] Could not delete {Path}", path); }
    }

    private static string Short(string? hash) => string.IsNullOrEmpty(hash) ? "(none)" : hash.Length > 12 ? hash[..12] : hash;

    private static PublishResult Fail(string message) => new() { Success = false, Message = message };
}
