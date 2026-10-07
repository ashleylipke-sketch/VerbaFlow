using System.Threading.Channels;

namespace VerbaFlow.Infrastructure.Services;

/// <summary>In-process queue (stand-in for Azure Service Bus). Items to be transcribed and analysed.</summary>
public sealed class ProcessingQueue
{
    private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>();
    public void Enqueue(Guid itemId) => _channel.Writer.TryWrite(itemId);
    public IAsyncEnumerable<Guid> ReadAllAsync(CancellationToken ct) => _channel.Reader.ReadAllAsync(ct);
}
