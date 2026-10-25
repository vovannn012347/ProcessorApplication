using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Newtonsoft.Json;

using ProcessingModule.Configuration;
using ProcessingModule.Database;
using ProcessingModule.Infrastructure;
using ProcessingModule.Infrastructure.Util;

namespace ProcessingModule.Services.Installation;

public enum ScriptInstallStatus
{
    /// <summary>The package has errors (or couldn't be read) and was discarded.</summary>
    Rejected,

    /// <summary>Valid and nothing conflicts: commit to install.</summary>
    Ready,

    /// <summary>The package is valid but the same script ID and version is already installed. Commit with overwrite to replace it.</summary>
    NeedsConfirmation,

    /// <summary>Installed and indexed.</summary>
    Installed,

    /// <summary>Valid package, but installing it failed (locked files, running jobs, indexing error). Nothing was changed.</summary>
    Failed
}

public sealed class ScriptInstallOptions
{
    /// <summary>Replace an installed script with the same ID and version.</summary>
    public bool Overwrite { get; init; }

    /// <summary>Where the package came from, for the report (file name, peer node...).</summary>
    public string? SourceName { get; init; }
}

public sealed class ScriptInstallResult
{
    [JsonProperty("stage_id")]
    public string? StageId { get; set; }

    [JsonProperty("status")]
    [JsonConverter(typeof(Newtonsoft.Json.Converters.StringEnumConverter))]
    public ScriptInstallStatus Status { get; set; }

    /// <summary>One-line summary for the UI.</summary>
    [JsonProperty("message")]
    public string Message { get; set; } = "";

    [JsonProperty("report")]
    public ScriptValidationReport Report { get; set; } = new();

    /// <summary>Folder name under ScriptSourcePath the script was installed to.</summary>
    [JsonProperty("installed_folder")]
    public string? InstalledFolder { get; set; }

    [JsonProperty("script_index_id")]
    public int? ScriptIndexId { get; set; }

    /// <summary>Other installed versions of the same script ID.</summary>
    [JsonProperty("other_versions")]
    public List<string> OtherVersions { get; set; } = new();

    [JsonProperty("index_log")]
    public List<string> IndexLog { get; set; } = new();
}

/// <summary>
/// The one path for adding scripts to the library: admin upload today, ScriptProvisioning (P2P) later.
/// Stage = save archive, extract safely, validate. Commit = move into ScriptSourcePath/&lt;label&gt;_&lt;version&gt;/ and index.
/// </summary>
public interface IScriptInstaller
{
    /// <summary>Saves the .zip content, extracts it with size limits and validates it. Nothing is installed yet.</summary>
    Task<ScriptInstallResult> StageAsync(Stream zipContent, string? sourceName, CancellationToken ct = default);

    /// <summary>Installs a staged package. <paramref name="overwrite"/> must be true to replace the same ID and version.</summary>
    Task<ScriptInstallResult> CommitAsync(string stageId, bool overwrite, HttpContext? context = null, CancellationToken ct = default);

    /// <summary>Deletes a staged package.</summary>
    void Discard(string stageId);

    /// <summary>Stage + commit in one call, for callers without a confirmation step.</summary>
    Task<ScriptInstallResult> InstallAsync(Stream zipContent, ScriptInstallOptions options, CancellationToken ct = default);
}

public sealed class ScriptInstaller : IScriptInstaller
{
    public const string StagingFolderName = ".staging";
    private const string PackageFileName = "package.zip";
    private const string ContentFolderName = "content";
    private const string StateFileName = "stage.json";
    private static readonly TimeSpan StageLifetime = TimeSpan.FromHours(6);

    // One install at a time: folder moves and the DB update must not interleave.
    private static readonly SemaphoreSlim CommitLock = new(1, 1);

    private static readonly string[] FinishedStatuses =
    {
        TaskStatusKeyword.Complete, TaskStatusKeyword.Warning, TaskStatusKeyword.Error,
        TaskStatusKeyword.ServerError, TaskStatusKeyword.Stopped
    };

    private readonly ProcessorDbContext _db;
    private readonly IScriptIndexer _indexer;
    private readonly IOptionsMonitor<ProcessorSettings> _settings;
    private readonly ILogger<ScriptInstaller> _logger;

    public ScriptInstaller(ProcessorDbContext db, IScriptIndexer indexer,
        IOptionsMonitor<ProcessorSettings> settings, ILogger<ScriptInstaller> logger)
    {
        _db = db;
        _indexer = indexer;
        _settings = settings;
        _logger = logger;
    }

    private sealed class StageState
    {
        public string StageId { get; set; } = "";
        public string? SourceName { get; set; }
        public DateTime CreatedUtc { get; set; }
        /// <summary>Script folder relative to the content folder: "" or one subfolder name.</summary>
        public string ScriptRoot { get; set; } = "";
        public ScriptValidationReport Report { get; set; } = new();
    }

    private string ScriptRoot => Path.GetFullPath(_settings.CurrentValue.ScriptSourcePath);
    private string StagingRoot => Path.Combine(ScriptRoot, StagingFolderName);

    // ------------------------------------------------------------------ Stage

    public async Task<ScriptInstallResult> StageAsync(Stream zipContent, string? sourceName, CancellationToken ct = default)
    {
        var settings = _settings.CurrentValue;
        Directory.CreateDirectory(ScriptRoot);
        CleanupStaleStages();

        string stageId = Guid.NewGuid().ToString("N");
        string stageDir = Path.Combine(StagingRoot, stageId);
        string contentDir = Path.Combine(stageDir, ContentFolderName);
        string zipPath = Path.Combine(stageDir, PackageFileName);
        Directory.CreateDirectory(stageDir);

        var result = new ScriptInstallResult { StageId = stageId };
        var report = result.Report;

        try
        {
            // 1. Save the upload, counting bytes (the request's Content-Length isn't trusted)
            long maxUpload = settings.MaxScriptUploadMb * 1024L * 1024L;
            long saved = await CopyWithLimitAsync(zipContent, zipPath, maxUpload, ct);
            report.Info("archive", $"Received {SafeArchiveExtractor.FormatBytes(saved)}{(string.IsNullOrEmpty(sourceName) ? "" : $" ({sourceName})")}.");

            // 2. Extract with limits on bytes actually written
            var limits = new ArchiveLimits
            {
                MaxTotalBytes = settings.MaxScriptExtractedMb * 1024L * 1024L,
                MaxEntryBytes = settings.MaxScriptEntryMb * 1024L * 1024L,
                MaxEntries = settings.MaxScriptArchiveEntries,
                MaxCompressionRatio = settings.MaxScriptCompressionRatio
            };
            var extracted = await Task.Run(() => SafeArchiveExtractor.Extract(zipPath, contentDir, limits, ct), ct);
            report.Info("archive", $"Extracted {extracted.FileCount} files ({SafeArchiveExtractor.FormatBytes(extracted.TotalBytes)}).");
            TryDelete(zipPath);

            // 3. Find the manifest: archive root, or a single top-level folder
            string? scriptRootRel = LocateScriptRoot(contentDir, out var locateError);
            if (scriptRootRel == null)
            {
                report.Error("archive", locateError!);
                return Reject(result, stageDir, "The package layout is not valid.");
            }

            // 4. Validate
            string scriptDir = Path.Combine(contentDir, scriptRootRel);
            var validation = await Task.Run(() => ScriptPackageValidator.Validate(scriptDir), ct);
            report.Items.AddRange(validation.Report.Items);
            CopyReportSummary(validation.Report, report);

            if (report.HasErrors || validation.Manifest == null)
            {
                return Reject(result, stageDir, "The package has errors and was not installed.");
            }

            // 5. Compare with what's installed
            await DescribeInstalledVersionsAsync(result, ct);

            SaveState(stageDir, new StageState
            {
                StageId = stageId,
                SourceName = sourceName,
                CreatedUtc = DateTime.UtcNow,
                ScriptRoot = scriptRootRel,
                Report = report
            });

            bool sameVersionInstalled = await _db.Scripts.AnyAsync(
                s => s.ScriptIdentifier == report.ScriptId && s.ScriptVersion == report.Version, ct);

            if (sameVersionInstalled)
            {
                result.Status = ScriptInstallStatus.NeedsConfirmation;
                result.Message = $"{report.ScriptId} version {report.Version} is already installed. Replace it?";
            }
            else
            {
                result.Status = ScriptInstallStatus.Ready;
                result.Message = "The package is valid.";
            }

            return result;
        }
        catch (ArchiveExtractionException ex)
        {
            report.Error("archive", ex.Message);
            return Reject(result, stageDir, "The archive was rejected.");
        }
        catch (OperationCanceledException)
        {
            TryDeleteDirectory(stageDir);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Staging script package failed");
            report.Error("archive", $"Unexpected error while reading the package: {ex.Message}");
            return Reject(result, stageDir, "The package could not be processed.");
        }
    }

    // ------------------------------------------------------------------ Commit

    public async Task<ScriptInstallResult> CommitAsync(string stageId, bool overwrite, HttpContext? context = null, CancellationToken ct = default)
    {
        var result = new ScriptInstallResult { StageId = stageId };

        if (!TryGetStageDir(stageId, out var stageDir) || !File.Exists(Path.Combine(stageDir, StateFileName)))
        {
            result.Status = ScriptInstallStatus.Failed;
            result.Message = "This upload has expired or was already installed. Upload the package again.";
            return result;
        }

        var state = JsonConvert.DeserializeObject<StageState>(await File.ReadAllTextAsync(Path.Combine(stageDir, StateFileName), ct))!;
        result.Report = state.Report;
        var report = state.Report;

        await CommitLock.WaitAsync(ct);
        try
        {
            string root = ScriptRoot;
            string sourceDir = Path.Combine(stageDir, ContentFolderName, state.ScriptRoot);
            if (!Directory.Exists(sourceDir))
            {
                result.Status = ScriptInstallStatus.Failed;
                result.Message = "The staged files are missing. Upload the package again.";
                return result;
            }

            var existing = await _db.Scripts.FirstOrDefaultAsync(
                s => s.ScriptIdentifier == report.ScriptId && s.ScriptVersion == report.Version, ct);

            if (existing != null && !overwrite)
            {
                await DescribeInstalledVersionsAsync(result, ct);
                result.Status = ScriptInstallStatus.NeedsConfirmation;
                result.Message = $"{report.ScriptId} version {report.Version} is already installed. Replace it?";
                return result;
            }

            string? replaceDir = null;
            if (existing != null)
            {
                // Jobs read the build's folder while they run. Older steps without a build ID
                // are matched by script_id (and version, when recorded).
                int buildId = existing.Id;
                string? version = report.Version;
                var activeJobs = await _db.ProcessingJobs
                    .Where(j => !FinishedStatuses.Contains(j.Status) &&
                                (j.ScriptIndexId == buildId ||
                                 (j.ScriptIndexId == null && j.ScriptId == report.ScriptId &&
                                  (j.ScriptVersion == null || j.ScriptVersion == version))))
                    .CountAsync(ct);
                if (activeJobs > 0)
                {
                    result.Status = ScriptInstallStatus.Failed;
                    result.Message = $"{activeJobs} queued or running job step(s) use this script. Wait for them to finish or stop them, then try again.";
                    return result;
                }

                string existingDir = Path.GetFullPath(Path.Combine(root, existing.ManifestDirectoryPath ?? ""));
                if (ScriptPathRules.IsInside(root, existingDir) && Directory.Exists(existingDir))
                    replaceDir = existingDir;
            }

            // Target folder: <label>_<version>, replacing the existing copy only if it lives there.
            string baseName = $"{ScriptPathRules.ToFolderNamePart(report.Label, "script")}_{ScriptPathRules.ToFolderNamePart(report.Version, "0")}";
            string targetDir = Path.Combine(root, baseName);
            bool replacingInPlace = replaceDir != null &&
                                    string.Equals(Path.GetFullPath(replaceDir), Path.GetFullPath(targetDir), ScriptPathRules.PathComparison);

            if (!replacingInPlace && Directory.Exists(targetDir))
            {
                int n = 2;
                while (Directory.Exists(Path.Combine(root, $"{baseName}_{n}"))) n++;
                targetDir = Path.Combine(root, $"{baseName}_{n}");
                report.Info("install", $"Folder {baseName} already exists and belongs to something else; using {Path.GetFileName(targetDir)}.");
            }

            // Move into place (same volume: the staging folder is inside ScriptSourcePath)
            string? backupDir = null;
            try
            {
                if (replacingInPlace)
                {
                    backupDir = Path.Combine(stageDir, "previous");
                    Directory.Move(targetDir, backupDir);
                }
                Directory.Move(sourceDir, targetDir);
            }
            catch (IOException ex)
            {
                // Put things back the way they were
                if (backupDir != null && Directory.Exists(backupDir) && !Directory.Exists(targetDir))
                    TryMove(backupDir, targetDir);

                result.Status = ScriptInstallStatus.Failed;
                result.Message = $"Couldn't move the script into place: {ex.Message}. Files in the old folder may be open in another program.";
                return result;
            }

            // Index just this folder
            try
            {
                var entry = await _indexer.PreciseReindexAsync(targetDir, context, line =>
                {
                    result.IndexLog.Add(line);
                    return Task.CompletedTask;
                }, saveDb: true);

                if (entry == null) throw new InvalidOperationException("The indexer could not read the installed manifest.");

                result.ScriptIndexId = entry.Id;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Indexing installed script failed; rolling back");
                _db.ChangeTracker.Clear();

                TryMove(targetDir, sourceDir);
                if (backupDir != null) TryMove(backupDir, targetDir);

                result.Status = ScriptInstallStatus.Failed;
                result.Message = $"Indexing failed, so the install was rolled back: {ex.Message}";
                return result;
            }

            result.InstalledFolder = Path.GetFileName(targetDir);

            if (replaceDir != null && !replacingInPlace)
            {
                report.Warn("install", $"The previous copy in '{Path.GetRelativePath(root, replaceDir)}' was left on disk. Delete it once you no longer need it.");
            }

            await DescribeInstalledVersionsAsync(result, ct);
            if (result.OtherVersions.Count > 0)
            {
                report.Info("install",
                    $"Other versions of this script are installed ({string.Join(", ", result.OtherVersions)}). " +
                    "Each version is a separate entry in the library; pick the one you want when starting a job.");
            }

            result.Status = ScriptInstallStatus.Installed;
            result.Message = existing != null
                ? $"Replaced {report.ScriptId} version {report.Version} in {result.InstalledFolder}."
                : $"Installed {report.ScriptId} version {report.Version} to {result.InstalledFolder}.";

            _logger.LogInformation("Script {ScriptId} v{Version} installed to {Folder} (source: {Source})",
                report.ScriptId, report.Version, result.InstalledFolder, state.SourceName);

            TryDeleteDirectory(stageDir);
            return result;
        }
        finally
        {
            CommitLock.Release();
        }
    }

    public void Discard(string stageId)
    {
        if (TryGetStageDir(stageId, out var stageDir)) TryDeleteDirectory(stageDir);
    }

    public async Task<ScriptInstallResult> InstallAsync(Stream zipContent, ScriptInstallOptions options, CancellationToken ct = default)
    {
        var staged = await StageAsync(zipContent, options.SourceName, ct);
        if (staged.StageId == null) return staged; // rejected

        if (staged.Status == ScriptInstallStatus.NeedsConfirmation && !options.Overwrite)
        {
            Discard(staged.StageId);
            staged.StageId = null;
            return staged;
        }

        var committed = await CommitAsync(staged.StageId, options.Overwrite, null, ct);
        if (committed.Status != ScriptInstallStatus.Installed) Discard(staged.StageId);
        return committed;
    }

    // ------------------------------------------------------------------ Helpers

    private async Task DescribeInstalledVersionsAsync(ScriptInstallResult result, CancellationToken ct)
    {
        var report = result.Report;
        result.OtherVersions = await _db.Scripts
            .Where(s => s.ScriptIdentifier == report.ScriptId && s.ScriptVersion != report.Version)
            .Select(s => s.ScriptVersion)
            .ToListAsync(ct);
    }

    private static ScriptInstallResult Reject(ScriptInstallResult result, string stageDir, string message)
    {
        result.Status = ScriptInstallStatus.Rejected;
        result.Message = message;
        result.StageId = null;
        TryDeleteDirectory(stageDir);
        return result;
    }

    private static void CopyReportSummary(ScriptValidationReport from, ScriptValidationReport to)
    {
        to.ScriptId = from.ScriptId;
        to.Version = from.Version;
        to.Label = from.Label;
        to.EntryPoint = from.EntryPoint;
        to.ComputedHash = from.ComputedHash;
        to.HashedFileCount = from.HashedFileCount;
        to.FileCount = from.FileCount;
        to.TotalBytes = from.TotalBytes;
    }

    /// <summary>Manifest at the archive root, or inside exactly one top-level folder (how most zip tools pack a folder).</summary>
    private static string? LocateScriptRoot(string contentDir, out string? error)
    {
        error = null;
        if (File.Exists(Path.Combine(contentDir, MicsConstants.ScriptManifestFile))) return "";

        var dirs = Directory.GetDirectories(contentDir)
            .Where(d => !string.Equals(Path.GetFileName(d), "__MACOSX", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var files = Directory.GetFiles(contentDir);

        if (dirs.Count == 1 && files.Length == 0 &&
            File.Exists(Path.Combine(dirs[0], MicsConstants.ScriptManifestFile)))
        {
            return Path.GetFileName(dirs[0]);
        }

        error = $"{MicsConstants.ScriptManifestFile} must be at the top of the archive or inside a single top-level folder.";
        return null;
    }

    private static async Task<long> CopyWithLimitAsync(Stream source, string targetPath, long maxBytes, CancellationToken ct)
    {
        var buffer = new byte[81920];
        long total = 0;
        await using var target = new FileStream(targetPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        int read;
        while ((read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
        {
            total += read;
            if (total > maxBytes)
                throw new ArchiveExtractionException($"The upload is larger than the limit of {SafeArchiveExtractor.FormatBytes(maxBytes)}.");
            await target.WriteAsync(buffer.AsMemory(0, read), ct);
        }
        if (total == 0) throw new ArchiveExtractionException("The uploaded file is empty.");
        return total;
    }

    private bool TryGetStageDir(string stageId, out string stageDir)
    {
        stageDir = "";
        // Strict format check: the ID is used as a folder name.
        if (!Guid.TryParseExact(stageId, "N", out var guid)) return false;
        stageDir = Path.Combine(StagingRoot, guid.ToString("N"));
        return Directory.Exists(stageDir);
    }

    private static void SaveState(string stageDir, StageState state) =>
        File.WriteAllText(Path.Combine(stageDir, StateFileName), JsonConvert.SerializeObject(state));

    private void CleanupStaleStages()
    {
        if (!Directory.Exists(StagingRoot)) return;
        foreach (var dir in Directory.GetDirectories(StagingRoot))
        {
            try
            {
                if (DateTime.UtcNow - Directory.GetCreationTimeUtc(dir) > StageLifetime) TryDeleteDirectory(dir);
            }
            catch (IOException) { }
        }
    }

    private static void TryMove(string from, string to)
    {
        try { if (Directory.Exists(from) && !Directory.Exists(to)) Directory.Move(from, to); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void TryDelete(string file)
    {
        try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static void TryDeleteDirectory(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
