using VerbaFlow.Infrastructure.Services;

namespace VerbaFlow.Api;

/// <summary>Retention rules: how long recordings are kept, holds, and what is due.</summary>
public static class RetentionEndpoints
{
    public static void MapRetentionEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/retention", (HttpContext h, RetentionService r) => r.OverviewAsync(h.Current()));
        api.MapPost("/retention/propose", (HttpContext h, RetentionService r, RetentionChange b) => r.ProposeAsync(h.Current(), b));
        api.MapPost("/retention/agree", (HttpContext h, RetentionService r) => r.AgreeAsync(h.Current()));
        api.MapPost("/retention/drop", (HttpContext h, RetentionService r) => r.DropProposalAsync(h.Current()));
        api.MapGet("/items/{id:guid}/retention", (HttpContext h, RetentionService r, Guid id) => r.ForItemAsync(h.Current(), id));
        api.MapPost("/items/{id:guid}/retention/hold", (HttpContext h, RetentionService r, Guid id, RetentionHoldBody b) => r.HoldAsync(h.Current(), id, b.Reason));
        api.MapPost("/items/{id:guid}/retention/release", (HttpContext h, RetentionService r, Guid id) => r.ReleaseAsync(h.Current(), id));
    }
}

/// <summary>Applies the retention rules every hour.</summary>
public sealed class RetentionWorker(RetentionService retention, FailureReporter reporter) : BackgroundService
{
    public static readonly TimeSpan Every = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(Every);
        do
        {
            try { await retention.ApplyAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { await reporter.ReportAsync("retention", null, ex); }
        } while (await timer.WaitForNextTickAsync(ct));
    }
}
