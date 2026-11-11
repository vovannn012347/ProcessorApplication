using ProcessingModule.Infrastructure;
using ProcessingModule.Services.Runtime;

namespace ProcessingModule.Services.Sandboxing;

/// <summary>
/// Stands in for a sandbox type this OS has no implementation for (e.g. Docker). Every step run with it ends in Error
/// with a clear message. Before, <see cref="SandboxProvider.GetActiveProcessor"/> threw outside the step's try/catch,
/// the step had already been saved as Running, and it stayed Running forever.
/// </summary>
public sealed class UnavailableSandbox : ISandboxProcessing
{
    private readonly SandboxType _type;
    private readonly string _reason;

    public UnavailableSandbox(SandboxType type, string reason)
    {
        _type = type;
        _reason = reason;
    }

    public SandboxType GetSandboxType() => _type;

    public Task ExecuteJobAsync(Guid subjobId) => throw new InvalidOperationException(_reason);

    public Task<int> GetActiveJobs() => Task.FromResult(0);

    public Task<string> GetJobStatusAsync(Guid jobId) => Task.FromResult("Unavailable");
}
