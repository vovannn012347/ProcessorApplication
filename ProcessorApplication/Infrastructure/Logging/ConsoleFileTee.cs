using System.Text;

namespace ProcessorApplication.Infrastructure.Logging;

/// <summary>
/// Copies everything written to the console (stdout and stderr: the logger output, [ModuleLoader] lines, errors)
/// into &lt;node home&gt;/logs/console-&lt;date&gt;.log, so a closed console window doesn't take the log with it.
/// The console itself still shows everything. A new file starts each day; a restart appends to today's file.
/// Off with --no-console-log or the environment variable PROCESSOR_CONSOLE_LOG=0.
/// Must run before the host is built: the console logger takes Console.Out once, when it is created.
/// </summary>
public static class ConsoleFileTee
{
    private static readonly object Gate = new();
    private static StreamWriter? _file;
    private static string _day = string.Empty;
    private static string _folder = string.Empty;

    public static void Start(string folder, string[] args)
    {
        if (args.Any(a => string.Equals(a, "--no-console-log", StringComparison.OrdinalIgnoreCase))) return;
        var env = Environment.GetEnvironmentVariable("PROCESSOR_CONSOLE_LOG");
        if (env == "0" || string.Equals(env, "false", StringComparison.OrdinalIgnoreCase)) return;

        _folder = folder;
        try
        {
            Console.SetOut(TextWriter.Synchronized(new TeeWriter(Console.Out)));
            Console.SetError(TextWriter.Synchronized(new TeeWriter(Console.Error)));
            WriteToFile($"===== {DateTime.Now:yyyy-MM-dd HH:mm:ss} start, pid {Environment.ProcessId} ====={Environment.NewLine}");

            // A crash prints its reason through the runtime's own stderr, not through Console.Error, so it never
            // reached this file (a node just vanished with nothing in its log). Write it here ourselves.
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
                WriteToFile($"{Environment.NewLine}===== {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} FATAL: unhandled exception, the process is terminating ====={Environment.NewLine}{e.ExceptionObject}{Environment.NewLine}");
            TaskScheduler.UnobservedTaskException += (_, e) =>
                WriteToFile($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [unobserved task exception] {e.Exception}{Environment.NewLine}");
            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
                WriteToFile($"===== {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} exit, code {Environment.ExitCode} ====={Environment.NewLine}");
        }
        catch
        {
            // a log file problem must never stop the node
        }
    }

    private static void WriteToFile(string text)
    {
        lock (Gate)
        {
            try
            {
                string day = DateTime.Now.ToString("yyyy-MM-dd");
                if (_file == null || day != _day)
                {
                    _file?.Dispose();
                    Directory.CreateDirectory(_folder);
                    var stream = new FileStream(Path.Combine(_folder, $"console-{day}.log"), FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                    _file = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
                    _day = day;
                }
                _file.Write(text);
            }
            catch
            {
                // disk full, file locked...: keep the console going
            }
        }
    }

    private sealed class TeeWriter : TextWriter
    {
        private readonly TextWriter _console;
        public TeeWriter(TextWriter console) => _console = console;

        public override Encoding Encoding => _console.Encoding;

        public override void Write(char value)
        {
            _console.Write(value);
            WriteToFile(value.ToString());
        }

        public override void Write(string? value)
        {
            if (value == null) return;
            _console.Write(value);
            WriteToFile(value);
        }

        public override void Write(char[] buffer, int index, int count)
        {
            _console.Write(buffer, index, count);
            WriteToFile(new string(buffer, index, count));
        }

        public override void WriteLine(string? value)
        {
            _console.WriteLine(value);
            WriteToFile((value ?? string.Empty) + Environment.NewLine);
        }

        public override void Flush() => _console.Flush();
    }
}
