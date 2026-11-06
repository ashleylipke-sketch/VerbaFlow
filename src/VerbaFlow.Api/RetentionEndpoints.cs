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

public static class NoteEndpoints
{
    public sealed record NoteBody(string? Text);
    public sealed record DoneBody(bool Done);

    public static void MapNoteEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/items/{id:guid}/notes", (HttpContext h, NoteService n, Guid id) => n.ListAsync(h.Current(), id));
        api.MapPost("/items/{id:guid}/notes", (HttpContext h, NoteService n, Guid id, NoteBody b) => n.AddAsync(h.Current(), id, b.Text));
        api.MapPut("/items/{id:guid}/notes/{nid:guid}/done", (HttpContext h, NoteService n, Guid id, Guid nid, DoneBody b) => n.SetDoneAsync(h.Current(), id, nid, b.Done));
        api.MapDelete("/items/{id:guid}/notes/{nid:guid}", (HttpContext h, NoteService n, Guid id, Guid nid) => n.RemoveAsync(h.Current(), id, nid));
    }
}

public static class CommentEndpoints
{
    public sealed record CommentBody(Guid SegmentId, int From, int To, string? Text);
    public sealed record ReplyBody(string? Text);
    public sealed record ResolvedBody(bool Resolved);

    public static void MapCommentEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/items/{id:guid}/comments", (HttpContext h, CommentService c, Guid id) => c.ListAsync(h.Current(), id));
        api.MapPost("/items/{id:guid}/comments", (HttpContext h, CommentService c, Guid id, CommentBody b) => c.AddAsync(h.Current(), id, b.SegmentId, b.From, b.To, b.Text));
        api.MapPost("/items/{id:guid}/comments/{cid:guid}/replies", (HttpContext h, CommentService c, Guid id, Guid cid, ReplyBody b) => c.ReplyAsync(h.Current(), id, cid, b.Text));
        api.MapPut("/items/{id:guid}/comments/{cid:guid}/resolved", (HttpContext h, CommentService c, Guid id, Guid cid, ResolvedBody b) => c.SetResolvedAsync(h.Current(), id, cid, b.Resolved));
        api.MapDelete("/items/{id:guid}/comments/{cid:guid}", (HttpContext h, CommentService c, Guid id, Guid cid) => c.RemoveAsync(h.Current(), id, cid));
    }
}
