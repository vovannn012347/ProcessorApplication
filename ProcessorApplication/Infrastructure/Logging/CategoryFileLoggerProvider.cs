using System.Text;

namespace ProcessorApplication.Infrastructure.Logging;

/// <summary>
/// Writes the log lines of some categories (by prefix) to a daily file instead of the console, e.g. every SQL
/// statement EF Core runs -> &lt;node home&gt;/logs/sql-2026-11-24.log. The console keeps those categories at Warning
/// (Logging:Console:LogLevel in appsettings.json), so errors still show there too.
/// </summary>
public sealed class CategoryFileLoggerProvider : ILoggerProvider
{
    private readonly string _folder;
    private readonly string _filePrefix;
    private readonly string[] _categoryPrefixes;
    private readonly object _gate = new();
    private StreamWriter? _writer;
    private string _writerDay = string.Empty;

    public CategoryFileLoggerProvider(string folder, string filePrefix, params string[] categoryPrefixes)
    {
        _folder = folder;
        _filePrefix = filePrefix;
        _categoryPrefixes = categoryPrefixes;
    }

    public ILogger CreateLogger(string categoryName)
    {
        bool wanted = _categoryPrefixes.Any(p => categoryName.StartsWith(p, StringComparison.Ordinal));
        return wanted ? new FileLogger(this, categoryName) : NullLogger.Instance;
    }

    internal void Write(string category, LogLevel level, string message, Exception? exception)
    {
        var now = DateTime.Now;
        var line = new StringBuilder()
            .Append(now.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append(" [").Append(level).Append("] ")
            .Append(category).Append(": ").Append(message);
        if (exception != null) line.AppendLine().Append(exception);

        lock (_gate)
        {
            try
            {
                string day = now.ToString("yyyy-MM-dd");
                if (_writer == null || day != _writerDay)
                {
                    _writer?.Dispose();
                    Directory.CreateDirectory(_folder);
                    var stream = new FileStream(Path.Combine(_folder, $"{_filePrefix}-{day}.log"), FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                    _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
                    _writerDay = day;
                }
                _writer.WriteLine(line.ToString());
            }
            catch
            {
                // a log file problem must never break the app
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }

    private sealed class FileLogger : ILogger
    {
        private readonly CategoryFileLoggerProvider _provider;
        private readonly string _category;

        public FileLogger(CategoryFileLoggerProvider provider, string category)
        {
            _provider = provider;
            _category = category;
        }

        public IDisposable BeginScope<TState>(TState state) => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            _provider.Write(_category, logLevel, formatter(state, exception), exception);
        }
    }

    private sealed class NullLogger : ILogger
    {
        public static readonly NullLogger Instance = new();
        public IDisposable BeginScope<TState>(TState state) => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => false;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) { }
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose() { }
    }
}
