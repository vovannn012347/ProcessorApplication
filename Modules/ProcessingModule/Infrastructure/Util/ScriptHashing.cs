using System.Security.Cryptography;
using System.Text;

using ProcessingModule.Models;

namespace ProcessingModule.Infrastructure.Util;

/// <summary>
/// Script integrity hash (artifact_hash).
///
/// Current method covers everything the executor loads from the script folder:
///   - the entry_point file
///   - every script_file / source_file input
///   - every file under each source_directory input
/// Each file contributes: relative path (forward slashes, UTF-8), 0x00, 8-byte length, content.
/// Including the path means renaming or swapping files changes the hash; including the length
/// means file boundaries can't be shifted between files.
///
/// The legacy method (before October 2026) hashed only inputs with a disk_path, content only,
/// and missed the entry point. It's kept so existing hashes can be recognised and upgraded.
/// </summary>
public static class ScriptHashing
{
    public sealed class HashedFileSet
    {
        /// <summary>Absolute paths, deduplicated, in hashing order.</summary>
        public List<string> Files { get; } = new();

        /// <summary>Readable problems (missing file, path outside folder). Non-empty means the script can't be hashed.</summary>
        public List<string> Problems { get; } = new();

        public bool IsComplete => Problems.Count == 0;
    }

    /// <summary>Lists the files covered by the hash for a script folder.</summary>
    public static HashedFileSet ResolveHashedFiles(ScriptManifest manifest, string scriptDir)
    {
        var set = new HashedFileSet();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddFile(string fullPath)
        {
            if (seen.Add(fullPath)) set.Files.Add(fullPath);
        }

        // 1. Entry point
        if (string.IsNullOrWhiteSpace(manifest.EntryPoint))
        {
            set.Problems.Add("entry_point is missing from the manifest");
        }
        else if (!ScriptPathRules.TryResolveInside(scriptDir, manifest.EntryPoint, out var entryFull, out var entryError))
        {
            set.Problems.Add($"entry_point '{manifest.EntryPoint}': {entryError}");
        }
        else if (!File.Exists(entryFull))
        {
            set.Problems.Add($"entry_point file not found: {manifest.EntryPoint}");
        }
        else
        {
            AddFile(entryFull);
        }

        // 2. Source inputs
        foreach (var input in manifest.Inputs ?? new List<InputMapping>())
        {
            if (!InOutType.SourceInput.Contains(input.Type)) continue;

            if (string.IsNullOrWhiteSpace(input.DiskPath))
            {
                set.Problems.Add($"input '{input.Label}' ({input.Type}) has no disk_path");
                continue;
            }

            if (!ScriptPathRules.TryResolveInside(scriptDir, input.DiskPath, out var full, out var error))
            {
                set.Problems.Add($"input '{input.Label}' disk_path '{input.DiskPath}': {error}");
                continue;
            }

            if (input.Type == InOutType.SourceDir)
            {
                if (!Directory.Exists(full))
                {
                    set.Problems.Add($"source directory not found: {input.DiskPath}");
                    continue;
                }

                foreach (var file in EnumerateFilesNoLinks(full)) AddFile(file);
            }
            else
            {
                if (!File.Exists(full))
                {
                    set.Problems.Add($"source file not found: {input.DiskPath}");
                    continue;
                }

                AddFile(full);
            }
        }

        return set;
    }

    /// <summary>Current hash method. Call only when <see cref="HashedFileSet.IsComplete"/> is true.</summary>
    public static string ComputeHash(string scriptDir, IEnumerable<string> files)
    {
        string root = Path.GetFullPath(scriptDir);

        var ordered = files
            .Select(f => (Full: f, Rel: Path.GetRelativePath(root, f).Replace('\\', '/')))
            .OrderBy(x => x.Rel, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Rel, StringComparer.Ordinal)
            .ToList();

        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        var lengthBytes = new byte[8];

        foreach (var (full, rel) in ordered)
        {
            sha.AppendData(Encoding.UTF8.GetBytes(rel));
            sha.AppendData(new byte[] { 0 });

            using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan);
            BitConverter.TryWriteBytes(lengthBytes, stream.Length);
            if (!BitConverter.IsLittleEndian) Array.Reverse(lengthBytes);
            sha.AppendData(lengthBytes);

            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                sha.AppendData(buffer, 0, read);
            }
        }

        return Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
    }

    /// <summary>
    /// The pre-October-2026 method, byte for byte: every input with a disk_path (any type),
    /// combined with the folder, sorted by full path, content + 0x00, missing files skipped.
    /// </summary>
    public static string ComputeLegacyHash(ScriptManifest manifest, string scriptDir)
    {
        var files = (manifest.Inputs ?? new List<InputMapping>())
            .Where(m => !string.IsNullOrEmpty(m.DiskPath))
            .Select(m => Path.Combine(scriptDir, m.DiskPath));

        using var sha256 = SHA256.Create();
        foreach (var path in files.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(path)) continue;

            using var stream = File.OpenRead(path);
            var buffer = new byte[8192];
            int bytesRead;
            while ((bytesRead = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                sha256.TransformBlock(buffer, 0, bytesRead, null, 0);
            }
            sha256.TransformBlock(new byte[] { 0 }, 0, 1, null, 0);
        }
        sha256.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Convert.ToHexString(sha256.Hash!).ToLowerInvariant();
    }

    public static bool HashEquals(string? a, string? b) =>
        !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b) && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>All files below a folder, not following symlinks/junctions.</summary>
    private static IEnumerable<string> EnumerateFilesNoLinks(string dir)
    {
        var pending = new Stack<string>();
        pending.Push(dir);

        while (pending.Count > 0)
        {
            var current = pending.Pop();
            foreach (var file in Directory.EnumerateFiles(current))
            {
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) == 0) yield return Path.GetFullPath(file);
            }
            foreach (var sub in Directory.EnumerateDirectories(current))
            {
                if ((File.GetAttributes(sub) & FileAttributes.ReparsePoint) == 0) pending.Push(sub);
            }
        }
    }
}
