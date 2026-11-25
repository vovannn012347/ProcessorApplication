using Common.Infrastructure;

namespace ProcessorApplication.Services.User;

/// <summary>
/// A JSON file with the first Admin account, so headless nodes need no browser for the first-run admin registration.
/// Used only while the node has no Admin yet; the node creates the Admin from it with its own keys.
///
/// Where it is looked for (first match wins):
///   1. the path in the AdminFile setting: command line  --AdminFile /path/admin.json
///      or environment  AdminFile=/path/admin.json   (shared: kept after use)
///   2. the node home: admin.json, then admin-seed.json (the swarm's name)   (deleted after use)
///   3. the program folder: admin.json, shared by every node started from it   (kept after use)
/// "DeleteAfterUse": true/false in the file overrides the default. A shared file holds a password in clear text:
/// delete it once all nodes have started.
///
/// Contents: { "Name": "Admin", "Surname": "", "Email": "admin@example.com", "Password": "..." }
/// </summary>
public sealed class AdminSeedFile
{
    public const string FileName = "admin.json";
    public const string LegacyFileName = "admin-seed.json";
    public const string SettingKey = "AdminFile";

    public string Path { get; }
    /// <summary>Explicit path or program folder: other nodes may still need it, so it is kept unless the file says otherwise.</summary>
    public bool IsShared { get; }
    /// <summary>Set when an explicit AdminFile path was given but no file is there.</summary>
    public string? MissingExplicitPath { get; }

    private AdminSeedFile(string path, bool isShared, string? missingExplicitPath = null)
    {
        Path = path;
        IsShared = isShared;
        MissingExplicitPath = missingExplicitPath;
    }

    public bool Exists => !string.IsNullOrEmpty(Path) && File.Exists(Path);

    /// <summary>Finds the file; returns an object whose <see cref="Exists"/> is false when there is none.</summary>
    public static AdminSeedFile Locate(string? explicitPath)
    {
        string? missing = null;
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            string full = System.IO.Path.GetFullPath(NodeHome.Resolve(explicitPath.Trim()));
            if (File.Exists(full)) return new AdminSeedFile(full, isShared: true);
            missing = full;
        }

        foreach (var name in new[] { FileName, LegacyFileName })
        {
            string inHome = System.IO.Path.Combine(NodeHome.Folder, name);
            if (File.Exists(inHome)) return new AdminSeedFile(inHome, isShared: false, missing);
        }

        if (NodeHome.IsSeparate)
        {
            string inApp = System.IO.Path.Combine(NodeHome.AppFolder, FileName);
            if (File.Exists(inApp)) return new AdminSeedFile(inApp, isShared: true, missing);
        }

        return new AdminSeedFile(string.Empty, isShared: false, missing);
    }
}
