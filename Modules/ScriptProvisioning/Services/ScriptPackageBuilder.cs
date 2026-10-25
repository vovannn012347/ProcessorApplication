using System.IO.Compression;

namespace ScriptProvisioning.Services;

/// <summary>
/// Packs an installed build folder into a zip the installer accepts (manifest at the archive root).
/// The output is deterministic: same files -> same bytes -> same transfer ContentHash, so publishing a build
/// again (or the same build from two nodes) doesn't create a second copy in the swarm.
/// </summary>
public static class ScriptPackageBuilder
{
    // Fixed entry time (zip can't store anything before 1980)
    private static readonly DateTimeOffset EntryTime = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Files that are never part of a package: Python caches and hidden/system folders.</summary>
    public static bool IsExcluded(string relativePath)
    {
        foreach (var part in relativePath.Split('/'))
        {
            if (part.StartsWith('.') || part.Equals("__pycache__", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return relativePath.EndsWith(".pyc", StringComparison.OrdinalIgnoreCase)
            || relativePath.EndsWith(".pyo", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Writes the package to <paramref name="outputPath"/> (via a temp file). Returns the number of files packed.</summary>
    public static int Build(string buildFolder, string outputPath)
    {
        string root = Path.GetFullPath(buildFolder);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException($"Build folder not found: {root}");

        var files = new List<(string Rel, string Full)>();
        Collect(root, root, files);
        files.Sort((a, b) => string.CompareOrdinal(a.Rel, b.Rel));

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        string temp = outputPath + ".tmp";
        try
        {
            using (var fs = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                foreach (var (rel, full) in files)
                {
                    var entry = zip.CreateEntry(rel, CompressionLevel.Optimal);
                    entry.LastWriteTime = EntryTime;
                    using var src = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read);
                    using var dst = entry.Open();
                    src.CopyTo(dst);
                }
            }
            File.Move(temp, outputPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
        return files.Count;
    }

    private static void Collect(string root, string dir, List<(string, string)> files)
    {
        foreach (var file in Directory.EnumerateFiles(dir))
        {
            // Links could point outside the build folder; the installer refuses them anyway
            if (new FileInfo(file).Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
            string rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (!IsExcluded(rel)) files.Add((rel, file));
        }
        foreach (var sub in Directory.EnumerateDirectories(dir))
        {
            if (new DirectoryInfo(sub).Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
            string rel = Path.GetRelativePath(root, sub).Replace('\\', '/');
            if (!IsExcluded(rel)) Collect(root, sub, files);
        }
    }
}
