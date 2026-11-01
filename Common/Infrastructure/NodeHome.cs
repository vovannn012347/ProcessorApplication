using System;
using System.IO;
using System.Linq;

namespace Common.Infrastructure;

/// <summary>
/// The node home: the folder that holds everything belonging to ONE running node - its databases,
/// Module_Data (P2P identity, consensus, files, keys), and its own settings files. The program files
/// (DLLs, views, wwwroot, shared appsettings) stay in the app folder and are shared by every node
/// started from it.
///
/// Chosen with "--node-home &lt;folder&gt;" (or "--node-home=&lt;folder&gt;"), or the PROCESSOR_NODE_HOME
/// environment variable. A relative folder is taken relative to the folder the process was started in.
/// Without either, the node home is the app folder itself (the old single-node layout).
///
/// Settings files in a node home use the same names and places as in the app folder and are loaded
/// after the app folder's files, so they override them:
///   {home}/appsettings.json, {home}/appsettings.{env}.json
///   {home}/appsettings.main.{env}.json
///   {home}/Modules/{ModuleFolder}/appsettings.{module}.{env}.json
/// </summary>
public static class NodeHome
{
    public const string ArgumentName = "--node-home";
    public const string EnvironmentVariable = "PROCESSOR_NODE_HOME";

    private static string? _folder;

    /// <summary>Where the program files are (shared by every node started from this build).</summary>
    public static string AppFolder { get; } = Normalize(AppContext.BaseDirectory);

    /// <summary>This node's own folder. Equals <see cref="AppFolder"/> when no node home was given.</summary>
    public static string Folder => _folder ?? AppFolder;

    /// <summary>True when this node runs from its own folder, separate from the program files.</summary>
    public static bool IsSeparate => !string.Equals(Folder, AppFolder, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Reads the node home from the arguments / environment, creates it and makes it the current directory,
    /// so every relative data path ("./Module_Data/...", "Data Source=medicalsystem.db") lands in it.
    /// Call first thing in Main, before anything reads or writes a file.
    /// </summary>
    public static string Initialize(string[] args)
    {
        string? value = null;
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (a.StartsWith(ArgumentName + "=", StringComparison.OrdinalIgnoreCase))
            {
                value = a.Substring(ArgumentName.Length + 1);
            }
            else if (string.Equals(a, ArgumentName, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                value = args[i + 1];
            }
        }
        if (string.IsNullOrWhiteSpace(value))
        {
            value = Environment.GetEnvironmentVariable(EnvironmentVariable);
        }

        if (!string.IsNullOrWhiteSpace(value))
        {
            string full = Normalize(Path.GetFullPath(value.Trim().Trim('"')));
            Directory.CreateDirectory(full);
            _folder = full;
        }

        Directory.SetCurrentDirectory(Folder);
        return Folder;
    }

    /// <summary>A path for this node's data: rooted paths are kept, relative ones are placed in the node home.</summary>
    public static string Resolve(string path)
        => Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(Folder, path));

    /// <summary>
    /// The node-home counterpart of a path inside the app folder (e.g. a module folder that keeps a database):
    /// same relative path, under the node home. Paths outside the app folder, or no separate home, are returned unchanged.
    /// </summary>
    public static string MapFromAppFolder(string appPath)
    {
        if (!IsSeparate) return appPath;
        string full = Path.GetFullPath(appPath);
        string root = AppFolder + Path.DirectorySeparatorChar;
        if (string.Equals(full.TrimEnd(Path.DirectorySeparatorChar), AppFolder, StringComparison.OrdinalIgnoreCase)) return Folder;
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return appPath;
        return Path.Combine(Folder, full.Substring(root.Length));
    }

    private static string Normalize(string path) => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
