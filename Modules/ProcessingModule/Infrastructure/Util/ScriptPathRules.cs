using System.Text.RegularExpressions;

namespace ProcessingModule.Infrastructure.Util;

/// <summary>
/// Rules for relative paths that come from untrusted script packages: zip entry names,
/// manifest disk_path / entry_point values and localization file names.
/// Mirrors the executor's make_path_safe/validate_path checks, plus Windows-specific traps.
/// </summary>
public static class ScriptPathRules
{
    public const int MaxRelativePathLength = 240;

    private static readonly char[] InvalidChars = { '<', '>', ':', '"', '|', '?', '*' };

    private static readonly Regex ReservedDeviceName = new(
        @"^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])(\..*)?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// Normalizes a relative path to forward slashes without "." segments.
    /// Returns false (with a readable reason) if the path is absolute, escapes upwards,
    /// or contains names Windows can't store or would silently alias.
    /// </summary>
    public static bool TryNormalizeRelative(string? path, out string normalized, out string error)
    {
        normalized = string.Empty;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(path))
        {
            error = "path is empty";
            return false;
        }

        string p = path.Replace('\\', '/');

        if (p.StartsWith('/'))
        {
            error = "absolute paths are not allowed";
            return false;
        }

        if (p.Length > MaxRelativePathLength)
        {
            error = $"path is longer than {MaxRelativePathLength} characters";
            return false;
        }

        var segments = new List<string>();
        foreach (var segment in p.Split('/'))
        {
            if (segment.Length == 0 || segment == ".") continue;

            if (segment == "..")
            {
                error = "'..' segments are not allowed";
                return false;
            }

            foreach (char c in segment)
            {
                if (c < 0x20 || Array.IndexOf(InvalidChars, c) >= 0)
                {
                    error = c < 0x20
                        ? "control characters are not allowed"
                        : $"character '{c}' is not allowed (drive letters and alternate streams are rejected too)";
                    return false;
                }
            }

            // Windows strips trailing dots/spaces, so "a.py." and "a.py" would be the same file.
            if (segment.EndsWith('.') || segment.EndsWith(' '))
            {
                error = $"name '{segment}' ends with a dot or space";
                return false;
            }

            if (ReservedDeviceName.IsMatch(segment))
            {
                error = $"'{segment}' is a reserved Windows device name";
                return false;
            }

            segments.Add(segment);
        }

        if (segments.Count == 0)
        {
            error = "path is empty";
            return false;
        }

        normalized = string.Join('/', segments);
        return true;
    }

    /// <summary>
    /// Resolves a relative path under <paramref name="rootDir"/> and confirms the result stays inside it.
    /// </summary>
    public static bool TryResolveInside(string rootDir, string? relativePath, out string fullPath, out string error)
    {
        fullPath = string.Empty;
        if (!TryNormalizeRelative(relativePath, out var normalized, out error)) return false;

        string root = EnsureTrailingSeparator(Path.GetFullPath(rootDir));
        string candidate = Path.GetFullPath(Path.Combine(root, normalized.Replace('/', Path.DirectorySeparatorChar)));

        if (!candidate.StartsWith(root, PathComparison))
        {
            error = "path resolves outside the script folder";
            return false;
        }

        fullPath = candidate;
        return true;
    }

    /// <summary>True if <paramref name="path"/> is <paramref name="rootDir"/> itself or somewhere below it.</summary>
    public static bool IsInside(string rootDir, string path, bool allowRootItself = false)
    {
        string root = Path.GetFullPath(rootDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (string.Equals(root, full, PathComparison)) return allowRootItself;
        return full.StartsWith(root + Path.DirectorySeparatorChar, PathComparison);
    }

    /// <summary>Turns a manifest value (label, version) into a safe single folder-name part.</summary>
    public static string ToFolderNamePart(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;

        var chars = value.Trim().Select(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.' ? c : '_').ToArray();
        string result = new string(chars).Trim('.', ' ');
        if (result.Length > 60) result = result[..60].TrimEnd('.', ' ');
        if (result.Length == 0 || ReservedDeviceName.IsMatch(result)) return fallback;
        return result;
    }

    public static string EnsureTrailingSeparator(string path) =>
        path.EndsWith(Path.DirectorySeparatorChar) ? path : path + Path.DirectorySeparatorChar;

    public static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
