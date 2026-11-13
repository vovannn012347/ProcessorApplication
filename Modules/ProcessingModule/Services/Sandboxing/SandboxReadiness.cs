namespace ProcessingModule.Services.Sandboxing;

/// <summary>
/// Result of the last OS-user sandbox check (at startup when ValidateSandboxOnStartup is on, or the Check Status
/// button in Processing settings). <see cref="SandboxProvider"/> uses it for RequireSandboxing: while the last check
/// failed, OS-user steps end in Error instead of being started. Unknown (never checked) never blocks.
/// </summary>
public sealed class SandboxReadiness
{
    private readonly object _lock = new();
    private List<string> _lines = new();
    private List<string> _failures = new();

    /// <summary>When the last check finished (UTC); null = not checked since the app started.</summary>
    public DateTime? CheckedUtc { get; private set; }

    /// <summary>True when the last check finished with no [FAIL]/[ABSENT]/[ERROR]/[CRITICAL] line.</summary>
    public bool? Ok { get; private set; }

    /// <summary>"startup" or "Check Status".</summary>
    public string Source { get; private set; } = "";

    public int WarningCount { get; private set; }

    public IReadOnlyList<string> Failures { get { lock (_lock) return _failures.ToList(); } }

    public IReadOnlyList<string> Lines { get { lock (_lock) return _lines.ToList(); } }

    public static bool IsFailure(string line) =>
        line.StartsWith("[FAIL]") || line.StartsWith("[ABSENT]") || line.StartsWith("[ERROR]") || line.StartsWith("[CRITICAL]");

    /// <summary>Records a finished check from its log lines.</summary>
    public void Record(IEnumerable<string> lines, string source)
    {
        var all = lines.ToList();
        lock (_lock)
        {
            _lines = all;
            _failures = all.Where(IsFailure).ToList();
            WarningCount = all.Count(l => l.StartsWith("[WARN]"));
            Ok = _failures.Count == 0;
            Source = source;
            CheckedUtc = DateTime.UtcNow;
        }
    }

    /// <summary>Forgets the result (e.g. after Provision, whose effect is only known after the next check).</summary>
    public void Reset()
    {
        lock (_lock)
        {
            _lines = new(); _failures = new(); WarningCount = 0;
            Ok = null; Source = ""; CheckedUtc = null;
        }
    }
}
