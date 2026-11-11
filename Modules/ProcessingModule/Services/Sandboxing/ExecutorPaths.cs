namespace ProcessingModule.Services.Sandboxing;

/// <summary>
/// Where the standalone executor is. <c>PythonExecutablePath</c> may name the executor folder or the executable itself;
/// the executable is <c>executor.exe</c> on Windows and <c>executor</c> (PyInstaller build, no extension) elsewhere.
/// </summary>
public static class ExecutorPaths
{
    public static string ExecutableName => OperatingSystem.IsWindows() ? "executor.exe" : "executor";

    /// <summary>Full path of the executable; <see cref="FileNotFoundException"/> if it isn't there (scheduling halts on that).</summary>
    public static string Resolve(string? configuredPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
            throw new InvalidOperationException("Python/Executor Path is not configured.");

        string path = Path.GetFullPath(configuredPath);
        if (Directory.Exists(path))
            path = Path.Combine(path, ExecutableName);

        if (!File.Exists(path))
            throw new FileNotFoundException($"Critical Error: Python Executor not found at {path}");

        return path;
    }

    /// <summary>The folder the executable lives in (everything the executor loads, e.g. PyInstaller's _internal, is under it).</summary>
    public static string? GetDirectory(string? configuredPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
            return null;

        string path = Path.GetFullPath(configuredPath);
        return Directory.Exists(path) ? Path.TrimEndingDirectorySeparator(path) : Path.GetDirectoryName(path);
    }
}
