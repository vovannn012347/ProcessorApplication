using System.Collections.Concurrent;
using System.Threading.Channels;

namespace ProcessorModule.Services.Processing;
public class TaskControlMonitor
{
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _activeTasks = new();

    public CancellationToken GetToken(Guid taskId)
    {
        var cts = _activeTasks.GetOrAdd(taskId, _ => new CancellationTokenSource());
        return cts.Token;
    }

    public void Stop(Guid taskId)
    {
        if (_activeTasks.TryRemove(taskId, out var cts))
        {
            cts.Cancel(); // This triggers the CancellationToken in the ProcessingService
            cts.Dispose();
        }
    }

    public void Remove(Guid taskId) => _activeTasks.TryRemove(taskId, out _);
}