using VerbaFlow.Infrastructure.Services;

namespace VerbaFlow.Api;

/// <summary>Background worker that runs queued recordings and imports through processing.</summary>
public sealed class ProcessingWorker(ProcessingQueue queue, ProcessingService processing, ILogger<ProcessingWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await foreach (var id in queue.ReadAllAsync(ct))
        {
            try { await processing.ProcessAsync(id, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { log.LogError(ex, "Processing item {Id} failed unexpectedly", id); }
        }
    }
}
