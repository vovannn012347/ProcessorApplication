using System.Net.Http;

using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using ProcessorModule.Configuration;
using ProcessorModule.Database;
using ProcessorModule.Database.Models;
using ProcessorModule.Infrastructure;
using ProcessorModule.Models;

using HttpContent = Microsoft.AspNetCore.Http.HttpContext;
using Newtonsoft.Json;
using System.Security.Cryptography;


namespace ProcessorModule.Services;


public interface IScriptIndexer
{
    Task ContinuousReindexAsync(HttpContext context, Func<string, Task> log, CancellationToken ct);

    Task PreciseReindexAsync(string folderPath,
        HttpContext context = null,
        Func<string, Task> log = null,
        bool saveDb = false);

    List<ScriptIndex> GetAvailableScripts();
}

public class ScriptIndexer : IScriptIndexer
{
    private readonly ProcessorDbContext _db;
    private readonly IOptions<ProcessorSettings> _settings;

    public ScriptIndexer(ProcessorDbContext db, 
        IOptions<ProcessorSettings> settings)
    {
        _db = db;
        _settings = settings;
    }

    public List<ScriptIndex> GetAvailableScripts()
    {
        return _db.Scripts.ToList();
    }

    /// <summary>
    /// Performs a deep audit of indexed scripts and scans for new ones.
    /// </summary>
    public async Task ContinuousReindexAsync(HttpContext context, Func<string, Task> log, CancellationToken ct)
    {
        var root = _settings.Value.ScriptSourcePath;
        if (!Directory.Exists(root))
        {
            await log($"[ERROR] Root script directory not found: {root}");
            return;
        }

        await log("[INIT] Audit started.");

        // 1. Audit Existing DB Records
        var dbScripts = await _db.Scripts.ToListAsync(ct);
        var checkedFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var script in dbScripts)
        {
            if (ct.IsCancellationRequested) return;

            string folderName = Path.GetFileName(script.ManifestDirectoryPath) ?? "Unknown";
            string logPrefix = $"[DB_AUDIT] {script.ScriptLabel} (v{script.ScriptVersion}, ID: {script.Id})";

            // Track folder to prevent double-processing in the physical scan phase
            if (!string.IsNullOrEmpty(script.ManifestDirectoryPath))
            {
                checkedFolders.Add(Path.GetFullPath(script.ManifestDirectoryPath));
            }

            // Step A: Folder Presence
            if (!Directory.Exists(script.ManifestDirectoryPath))
            {
                script.IsAvailable = false;
                await log($"{logPrefix} [FAIL]: Directory not found at {script.ManifestDirectoryPath}. Disabled.");
                continue;
            }

            // Step B: Manifest Check
            var manifestPath = Path.Combine(script.ManifestDirectoryPath, "script_manifest.json");
            if (!File.Exists(manifestPath))
            {
                script.IsAvailable = false;
                await log($"{logPrefix} [FAIL]: manifest.json missing in folder. Disabled.");
                continue;
            }

            try
            {
                var json = await File.ReadAllTextAsync(manifestPath, ct);
                var manifest = JsonConvert.DeserializeObject<ScriptManifest>(json);

                if (manifest == null)
                {
                    script.IsAvailable = false;
                    await log($"{logPrefix} [FAIL]: Failed to deserialize manifest.");
                    continue;
                }

                // Step C: Source Files & Hash Check
                var manifestedFiles = manifest.Inputs
                    .Where(m => !string.IsNullOrEmpty(m.DiskPath))
                    .Select(m => Path.Combine(script.ManifestDirectoryPath, m.DiskPath))
                    .ToList();

                // Check if files actually exist on disk before hashing
                bool filesMissing = false;
                foreach (var file in manifestedFiles)
                {
                    if (!File.Exists(file))
                    {
                        string relFile = Path.GetRelativePath(root, file);
                        await log($"{logPrefix} [FAIL]: Source file missing: {relFile}");
                        filesMissing = true;
                    }
                }

                if (filesMissing)
                {
                    script.IsAvailable = false;
                    continue;
                }

                var currentHash = ComputeTotalHash(manifestedFiles);

                // Update DB state
                script.HashMatch = (currentHash == manifest.ScriptHash);

                if (!script.HashMatch && _settings.Value.UpdateHashOnMismatch)
                {
                    script.ArtifactHash = currentHash; // Update DB with latest re-calculated hash
                }
                script.ScriptVersion = manifest.ScriptVersion; // Keep version in sync
                script.IsAvailable = true;

                if (!script.HashMatch)
                {
                    await log($"{logPrefix} [WARN]: Hash mismatch detected. DB updated with new hash.");
                }
                else
                {
                    await log($"{logPrefix} [OK]: Verified and active.");
                }
            }
            catch (Exception ex)
            {
                await log($"{logPrefix} [ERROR]: Critical failure during check: {ex.Message}");
                script.IsAvailable = false;
            }
        }

        await _db.SaveChangesAsync(ct);

        // 2. Physical Scan for New Folders
        await log("[SCAN] Searching for new scripts...");
        var folders = Directory.GetDirectories(root);
        foreach (var folder in folders)
        {
            if (ct.IsCancellationRequested) break;

            string fullPath = Path.GetFullPath(folder);
            string relativePath = Path.GetRelativePath(root, fullPath);

            // Skip folders already handled by the DB audit (Match by path)
            if (checkedFolders.Contains(fullPath)) continue;

            // Load manifest to check ID/Version match
            var manifestPath = Path.Combine(fullPath, "script_manifest.json");
            if (!File.Exists(manifestPath))
            {
                await log($"[SCAN] [SKIP]: No manifest in folder {relativePath}");
                continue;
            }

            try
            {
                var json = await File.ReadAllTextAsync(manifestPath, ct);
                var manifest = JsonConvert.DeserializeObject<ScriptManifest>(json);

                if (manifest != null)
                {
                    // Check if this script+version combination is already in DB (even if path changed)
                    bool existsInDb = dbScripts.Any(s =>
                        s.ScriptIdentifier == manifest.ScriptId &&
                        s.ScriptVersion == manifest.ScriptVersion);

                    if (existsInDb)
                    {
                        continue; // Skip, already indexed
                    }

                    await log($"[SCAN] [NEW]: Found {manifest.ScriptId} v{manifest.ScriptVersion} at {relativePath}. Indexing...");
                    await PreciseReindexAsync(fullPath, context, log, false);
                }
            }
            catch (Exception ex)
            {
                await log($"[SCAN] [ERROR]: Failed to process {relativePath}: {ex.Message}");
            }
        }

        await _db.SaveChangesAsync(ct);
        await log("[FINISH] Library reindexing complete.");
    }

    public async Task PreciseReindexAsync(string folderPath,
        HttpContext context = null,
        Func<string, Task> log = null,
        bool saveDb = false)
    {
        var root = _settings.Value.ScriptSourcePath;
        string relativeFolder = Path.GetRelativePath(root, folderPath);

        var manifestPath = Path.Combine(folderPath, "script_manifest.json");
        if (!File.Exists(manifestPath))
        {
            if (log != null) await log($"[INDEX] [SKIP] No manifest found at {relativeFolder}");
            return;
        }

        var json = await File.ReadAllTextAsync(manifestPath);
        var manifest = JsonConvert.DeserializeObject<ScriptManifest>(json);

        if (manifest != null)
        {
            if (log != null) await log($"[INDEX] Processing {manifest.ScriptId} (v{manifest.ScriptVersion}) at {relativeFolder}...");

            var localizationFile = ResolveLocalizationFile(context, manifest);
            var localizationPath = Path.Combine(folderPath, MicsConstants.LocalizationDirectory, localizationFile);

            Dictionary<string, string> localization = new();
            if (File.Exists(localizationPath))
            {
                var lJson = await File.ReadAllTextAsync(localizationPath);
                localization = JsonConvert.DeserializeObject<Dictionary<string, string>>(lJson) ?? new();
                if (log != null) await log($"[INDEX] Applied localization: {localizationFile}");
            }

            string label = localization.TryGetValue(manifest.ScriptLabel, out var localizedLabel)
                ? localizedLabel
                : manifest.ScriptLabel;

            var manifestedFiles = manifest.Inputs
                .Where(m => !string.IsNullOrEmpty(m.DiskPath))
                .Select(m => Path.Combine(folderPath, m.DiskPath))
                .ToList();

            if (log != null) await log($"[INDEX] Calculating hash for {manifestedFiles.Count} files...");
            var artifactHash = ComputeTotalHash(manifestedFiles);

            bool hashMatch = string.IsNullOrEmpty(manifest.ScriptHash) || manifest.ScriptHash == artifactHash;

            if (string.IsNullOrEmpty(manifest.ScriptHash))
            {
                manifest.ScriptHash = artifactHash;
            }

            var existing = await _db.Scripts.FirstOrDefaultAsync(
                s => s.ScriptIdentifier == manifest.ScriptId && s.ScriptVersion == manifest.ScriptVersion);

            if (existing == null)
            {
                if (log != null) await log($"[INDEX] Adding new entry to database: {label}");
                _db.Scripts.Add(new ScriptIndex
                {
                    ScriptIdentifier = manifest.ScriptId,
                    ScriptLabel = label,
                    ScriptVersion = manifest.ScriptVersion,
                    ProcessorVersion = manifest.MinProcessorVersion,
                    ArtifactHash = artifactHash,
                    HashMatch = hashMatch,
                    IsAvailable = true,
                    ManifestDirectoryPath = folderPath,
                    CreatedTime = DateTime.UtcNow
                });
            }
            else
            {
                if (log != null) await log($"[INDEX] Updating existing entry (ID: {existing.Id}): {label}");
                existing.ScriptLabel = label;
                existing.ProcessorVersion = manifest.MinProcessorVersion;

                if(!hashMatch && _settings.Value.UpdateHashOnMismatch)
                {
                    existing.ArtifactHash = artifactHash;
                }
                existing.HashMatch = hashMatch;
                existing.IsAvailable = true;
                existing.ManifestDirectoryPath = folderPath;
            }

            if (saveDb)
            {
                await _db.SaveChangesAsync();
                if (log != null) await log($"[INDEX] Successfully saved {manifest.ScriptId} to database.");
            }
        }
        else
        {
            if (log != null) await log($"[INDEX] [ERROR] Could not parse manifest at {relativeFolder}");
        }
    }

    private string ResolveLocalizationFile(HttpContext httpContext, ScriptManifest manifest, string defaultLocale = "en")
    {
        var localization = manifest.Localization;
        if (localization == null || localization.Count == 0) return "";

        if (httpContext != null)
        {
            var acceptLanguages = httpContext.Request.GetTypedHeaders().AcceptLanguage?
                .OrderByDescending(l => l.Quality ?? 1.0);

            if (acceptLanguages != null)
            {
                foreach (var lang in acceptLanguages)
                {
                    var culture = lang.Value.Value;
                    if (string.IsNullOrEmpty(culture)) continue;
                    if (localization.TryGetValue(culture, out var file)) return file;

                    var neutral = culture.Split('-')[0];
                    if (localization.TryGetValue(neutral, out file)) return file;
                }
            }
        }

        return localization.TryGetValue(defaultLocale, out var fallback) ? fallback : localization.Values.First();
    }

    public static string ComputeTotalHash(IEnumerable<string> filePaths)
    {
        using var sha256 = SHA256.Create();
        foreach (var path in filePaths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            if (File.Exists(path))
            {
                using var stream = File.OpenRead(path);
                var buffer = new byte[8192];
                int bytesRead;
                while ((bytesRead = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    sha256.TransformBlock(buffer, 0, bytesRead, null, 0);
                }
                sha256.TransformBlock(new byte[] { 0 }, 0, 1, null, 0);
            }
        }
        sha256.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Convert.ToHexString(sha256.Hash!).ToLowerInvariant();
    }
}