using System.Text;
using System.Text.RegularExpressions;

namespace ScriptProvisioning.Services;

/// <summary>
/// File name of a published script package: <c>{script_id}@{version}~{first 12 of artifact_hash}.pscript.zip</c>.
/// script_id may contain '.', '_' and '-', but never '@' or '~', so the name splits unambiguously.
/// The name is only a hint for finding packages in the file catalogue: what gets installed is decided by the
/// package itself (its manifest and the hash the installer computes), and a package whose content doesn't
/// match its name is refused.
/// </summary>
public sealed class ScriptPackageName
{
    public const string Suffix = ".pscript.zip";
    public const int HashPrefixLength = 12;

    private static readonly Regex Pattern = new(
        @"^(?<id>[A-Za-z0-9][A-Za-z0-9._-]{0,127})@(?<ver>[A-Za-z0-9._+-]{1,64})~(?<hash>[0-9a-f]{12})\.pscript\.zip$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public string ScriptId { get; }
    /// <summary>Version as it appears in the name (characters a file name can't hold are replaced by '_').</summary>
    public string Version { get; }
    public string HashPrefix { get; }

    private ScriptPackageName(string scriptId, string version, string hashPrefix)
    {
        ScriptId = scriptId;
        Version = version;
        HashPrefix = hashPrefix.ToLowerInvariant();
    }

    public static string Build(string scriptId, string version, string artifactHash)
    {
        if (string.IsNullOrEmpty(artifactHash) || artifactHash.Length < HashPrefixLength)
            throw new ArgumentException("The build has no artifact hash.", nameof(artifactHash));
        return $"{scriptId}@{SafeVersion(version)}~{artifactHash[..HashPrefixLength].ToLowerInvariant()}{Suffix}";
    }

    public static ScriptPackageName? TryParse(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return null;
        var m = Pattern.Match(fileName);
        return m.Success ? new ScriptPackageName(m.Groups["id"].Value, m.Groups["ver"].Value, m.Groups["hash"].Value) : null;
    }

    /// <summary>True if a build with this id/version/hash is what the name describes.</summary>
    public bool Describes(string scriptId, string? version, string? artifactHash) =>
        string.Equals(ScriptId, scriptId, StringComparison.Ordinal)
        && string.Equals(Version, SafeVersion(version), StringComparison.Ordinal)
        && artifactHash != null
        && artifactHash.StartsWith(HashPrefix, StringComparison.OrdinalIgnoreCase);

    public static string SafeVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return "0";
        var sb = new StringBuilder(version.Length);
        foreach (char c in version.Trim())
            sb.Append(IsAsciiLetterOrDigit(c) || c is '.' or '_' or '+' or '-' ? c : '_');
        string s = sb.ToString();
        return s.Length > 64 ? s[..64] : s;
    }

    // char.IsAsciiLetterOrDigit is .NET 7+
    private static bool IsAsciiLetterOrDigit(char c) => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9';
}
