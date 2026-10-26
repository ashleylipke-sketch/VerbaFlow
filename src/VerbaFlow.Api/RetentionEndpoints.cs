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

/// <summary>Files attached to a recording.</summary>
public static class AttachmentEndpoints
{
    public static void MapAttachmentEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/items/{id:guid}/attachments", (HttpContext h, AttachmentService a, Guid id) => a.ListAsync(h.Current(), id));
        api.MapPost("/items/{id:guid}/attachments", async (HttpContext h, AttachmentService a, Guid id, CancellationToken ct) =>
        {
            var form = await h.Request.ReadFormAsync(ct);
            var files = form.Files.GetFiles("files");
            var streams = files.Select(f => f.OpenReadStream()).ToList();
            try { return Results.Ok(await a.AddAsync(h.Current(), id, files.Select((f, i) => new AttachmentUpload(streams[i], f.FileName)).ToList(), ct)); }
            finally { foreach (var s in streams) await s.DisposeAsync(); }
        }).DisableAntiforgery();
        api.MapGet("/items/{id:guid}/attachments/{aid:guid}", async (HttpContext h, AttachmentService a, Guid id, Guid aid, bool? view) =>
        {
            var f = await a.OpenAsync(h.Current(), id, aid);
            h.Response.Headers.CacheControl = "private, no-store";
            h.Response.Headers["X-Content-Type-Options"] = "nosniff";
            // Only PDFs and pictures open in the browser; everything else is downloaded.
            var inline = view == true && (f.ContentType == "application/pdf" || f.ContentType.StartsWith("image/"));
            return Results.File(f.Content, f.ContentType, inline ? null : f.FileName, enableRangeProcessing: false);
        });
        api.MapDelete("/items/{id:guid}/attachments/{aid:guid}", (HttpContext h, AttachmentService a, Guid id, Guid aid) => a.RemoveAsync(h.Current(), id, aid));
    }
}
