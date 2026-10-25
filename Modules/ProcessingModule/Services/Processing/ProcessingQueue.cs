using System.Threading.Channels;

namespace ProcessingModule.Services.Processing;

/// <summary>Wake-up signals for the step scheduler (a job was added or changed, or a step finished).</summary>
public class ProcessingQueue
{
    private readonly Channel<Guid> _queue = Channel.CreateUnbounded<Guid>();
    public ValueTask EnqueueAsync(Guid taskId) => _queue.Writer.WriteAsync(taskId);
    public ValueTask<Guid> DequeueAsync(CancellationToken ct) => _queue.Reader.ReadAsync(ct);
    public bool TryDequeue(out Guid taskId) => _queue.Reader.TryRead(out taskId);
}