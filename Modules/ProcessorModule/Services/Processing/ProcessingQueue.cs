using System.Threading.Channels;

namespace ProcessorModule.Services.Processing;

public class ProcessingQueue
{
    private readonly Channel<Guid> _queue = Channel.CreateUnbounded<Guid>();
    public ValueTask EnqueueAsync(Guid taskId) => _queue.Writer.WriteAsync(taskId);
    public ValueTask<Guid> DequeueAsync(CancellationToken ct) => _queue.Reader.ReadAsync(ct);
}