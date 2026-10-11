using System.Text.Json;
using VerbaFlow.Core.Domain;
using VerbaFlow.Infrastructure.Export;
using VerbaFlow.Infrastructure.Services;

namespace VerbaFlow.Api;

public static class Endpoints
{
    public sealed record AssignBody(Guid? UserId, string? Reason);
    public sealed record ReasonBody(string Reason);
    public sealed record TextBody(string Text);
    public sealed record EmailBody(string? Format, string? To, string? Note, string? Parts = null);
    public sealed record NameBody(string Name);
    public sealed record TermBody(string Text, string? Note);
    public sealed record LinesBody(string Lines);

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web)
        { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    public static void MapMeetingEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/me", (HttpContext h) => h.Current());
        api.MapGet("/languages", () => new { max = SpokenLanguageCatalogue.MaxPerRecording, languages = SpokenLanguageCatalogue.All });
        // Technical error detail for VerbaFlow support and developers only. Customers, including their administrators, are refused.
        api.MapGet("/support/errors", async (HttpContext h, Stores stores, string? reference) =>
        {
            if (!h.Current().IsSupport) throw new ForbiddenException("This is only available to VerbaFlow support.");
            var all = await stores.SupportErrors.ListAsync();
            return all.Where(e => reference is null || string.Equals(e.Reference, reference, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(e => e.At).Take(200).ToList();
        });
        api.MapGet("/users", (MeetingService s) => s.ListUsersAsync());
        api.MapGet("/policy", (PlatformPolicy p) => p);

        // Custom vocabulary: anyone signed in can read it, administrators change it.
        api.MapGet("/vocabulary", (VocabularyService v) => v.ListAsync());
        api.MapPost("/vocabulary", async (HttpContext h, VocabularyService v, TermBody b) => Results.Ok(await v.AddAsync(h.Current(), b.Text, b.Note)));
        api.MapPost("/vocabulary/import", async (HttpContext h, VocabularyService v, LinesBody b) => Results.Ok(await v.ImportAsync(h.Current(), b.Lines)));
        api.MapDelete("/vocabulary/{id:guid}", async (HttpContext h, VocabularyService v, Guid id) => { await v.RemoveAsync(h.Current(), id); return Results.NoContent(); });

        api.MapGet("/items", (HttpContext h, MeetingService s, string? statuses, bool? selfAssigned, string? search) =>
        {
            HashSet<ItemStatus>? set = null;
            if (!string.IsNullOrWhiteSpace(statuses))
                set = statuses.Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(x => Enum.Parse<ItemStatus>(x, true)).ToHashSet();
            return s.ListAsync(h.Current(), new ListQuery(set, selfAssigned, search));
        });
        api.MapGet("/items/{id:guid}", (HttpContext h, MeetingService s, Guid id) => s.GetItemAsync(h.Current(), id));

        // Capture. Audio arrives as multipart so it can be streamed to quarantine rather than held in memory.
        api.MapPost("/items/recordings", async (HttpContext h, MeetingService s, CancellationToken ct) =>
        {
            var f = await h.Request.ReadFormAsync(ct);
            var file = f.Files["audio"] ?? throw new DomainException("No audio was received.");
            var markers = string.IsNullOrWhiteSpace(f["markers"]) ? [] :
                JsonSerializer.Deserialize<List<MarkerInput>>(f["markers"].ToString(), Web) ?? [];
            await using var stream = file.OpenReadStream();
            var id = await s.CreateRecordingAsync(h.Current(), new RecordingUpload(stream, file.FileName, file.ContentType,
                f["name"].ToString(), int.TryParse(f["lengthMs"], out var l) ? l : 0,
                string.IsNullOrEmpty(f["language"]) ? "en" : f["language"].ToString(),
                f["consentNoticeGiven"] == "true", markers, f["spokenLanguages"].ToString(), int.TryParse(f["numSpeakers"], out var ns1) ? ns1 : null), ct);
            return Results.Ok(new { id });
        }).DisableAntiforgery();

        api.MapPost("/items/imports", async (HttpContext h, MeetingService s, CancellationToken ct) =>
        {
            var f = await h.Request.ReadFormAsync(ct);
            var file = f.Files["file"] ?? throw new DomainException("No file was received.");
            await using var stream = file.OpenReadStream();
            var id = await s.ImportAsync(h.Current(), new ImportUpload(stream, file.FileName, file.ContentType,
                string.IsNullOrWhiteSpace(f["name"]) ? null : f["name"].ToString(),
                int.TryParse(f["lengthMs"], out var l) ? l : 0,
                string.IsNullOrEmpty(f["language"]) ? "en" : f["language"].ToString(),
                f["rightsConfirmed"] == "true", f["sourceNote"].ToString(), f["spokenLanguages"].ToString(), int.TryParse(f["numSpeakers"], out var ns2) ? ns2 : null), ct);
            return Results.Ok(new { id });
        }).DisableAntiforgery();

        api.MapPost("/items/{id:guid}/rerun", async (HttpContext h, MeetingService s, Guid id, RerunRequest? body) => { await s.RerunAsync(h.Current(), id, body?.NumSpeakers, body?.SpokenLanguages, body?.DiscardEdits ?? false, body?.Method, body?.Audio); return Results.NoContent(); });
        api.MapPost("/items/{id:guid}/retry", async (HttpContext h, MeetingService s, Guid id, RetryRequest? body) => { await s.RetryAsync(h.Current(), id, body?.SpokenLanguages); return Results.NoContent(); });

        api.MapPut("/items/{id:guid}/details", async (HttpContext h, MeetingService s, Guid id, DetailsUpdate d) =>
        { await s.UpdateDetailsAsync(h.Current(), id, d); return Results.NoContent(); });
        api.MapPost("/items/{id:guid}/assign", async (HttpContext h, MeetingService s, Guid id, AssignBody b) =>
        { await s.AssignAsync(h.Current(), id, b.UserId, b.Reason); return Results.NoContent(); });
        api.MapPost("/items/{id:guid}/accept", async (HttpContext h, MeetingService s, Guid id) =>
        { await s.AcceptAsync(h.Current(), id); return Results.NoContent(); });
        api.MapPost("/items/{id:guid}/return", async (HttpContext h, MeetingService s, Guid id) =>
        { await s.ReturnAsync(h.Current(), id); return Results.NoContent(); });
        api.MapPost("/items/{id:guid}/approve", async (HttpContext h, MeetingService s, Guid id) =>
        { await s.ApproveAsync(h.Current(), id); return Results.NoContent(); });

        api.MapPost("/items/{id:guid}/reopen", async (HttpContext h, MeetingService s, Guid id, ReasonBody b) =>
            Results.Ok(new { id = await s.RequestReopenAsync(h.Current(), id, b.Reason) }));
        api.MapGet("/reopen", (HttpContext h, MeetingService s) => s.ListReopenRequestsAsync(h.Current()));
        api.MapPost("/items/{id:guid}/delete", async (HttpContext h, MeetingService s, Guid id, ReasonBody b) =>
        { await s.DeleteAsync(h.Current(), id, b.Reason); return Results.NoContent(); });
        api.MapPost("/items/{id:guid}/delete-request", async (HttpContext h, MeetingService s, Guid id, ReasonBody b) =>
            Results.Ok(new { id = await s.RequestDeleteAsync(h.Current(), id, b.Reason) }));
        api.MapGet("/delete-requests", (HttpContext h, MeetingService s) => s.ListDeleteRequestsAsync(h.Current()));
        api.MapPost("/delete-requests/{id:guid}/approve", async (HttpContext h, MeetingService s, Guid id) =>
            Results.Ok(new { deleted = await s.ApproveDeleteAsync(h.Current(), id) }));
        api.MapPost("/delete-requests/{id:guid}/reject", async (HttpContext h, MeetingService s, Guid id, ReasonBody b) =>
        { await s.RejectDeleteAsync(h.Current(), id, b.Reason); return Results.NoContent(); });
        api.MapPost("/reopen/{id:guid}/approve", async (HttpContext h, MeetingService s, Guid id) =>
            Results.Ok(new { newItemId = await s.ApproveReopenAsync(h.Current(), id) }));
        api.MapPost("/reopen/{id:guid}/reject", async (HttpContext h, MeetingService s, Guid id, ReasonBody b) =>
        { await s.RejectReopenAsync(h.Current(), id, b.Reason); return Results.NoContent(); });

        api.MapGet("/items/{id:guid}/transcript", (HttpContext h, MeetingService s, Guid id, int? version) =>
            s.GetTranscriptAsync(h.Current(), id, version));
        api.MapPut("/items/{id:guid}/segments/{segId:guid}", async (HttpContext h, MeetingService s, Guid id, Guid segId, TextBody b) =>
        { await s.EditSegmentAsync(h.Current(), id, segId, b.Text); return Results.NoContent(); });
        api.MapPut("/items/{id:guid}/speakers/{spId:guid}", async (HttpContext h, MeetingService s, Guid id, Guid spId, NameBody b) =>
        { await s.RenameSpeakerAsync(h.Current(), id, spId, b.Name); return Results.NoContent(); });
        api.MapPost("/items/{id:guid}/versions/{no:int}/restore", async (HttpContext h, MeetingService s, Guid id, int no) =>
        { await s.RestoreVersionAsync(h.Current(), id, no); return Results.NoContent(); });
        api.MapGet("/items/{id:guid}/outputs", (HttpContext h, MeetingService s, Guid id) => s.GetOutputsAsync(h.Current(), id));
        api.MapPost("/items/{id:guid}/outputs/regenerate", async (HttpContext h, MeetingService s, Guid id, CancellationToken ct) =>
        { await s.RegenerateOutputsAsync(h.Current(), id, ct); return Results.NoContent(); });

        // Playback needs only Read; download is the separate ExportAudio right. Range requests let the player seek.
        api.MapGet("/items/{id:guid}/audio", async (HttpContext h, MeetingService s, Guid id, bool? download) =>
        {
            var audio = await s.OpenAudioAsync(h.Current(), id, download == true);
            h.Response.RegisterForDisposeAsync(audio);
            return Results.Stream(audio.Content, audio.ContentType, download == true ? audio.FileName : null, enableRangeProcessing: true);
        });

        // Minutes as Word or PDF. The file's fingerprint is recorded, so a copy can be checked later for changes.
        // preview=true shows the file in the browser instead of saving it; parts=summary,minutes,actions,transcript,tone picks what goes in.
        api.MapGet("/items/{id:guid}/export", async (HttpContext h, ExportService s, Guid id, string? format, bool? preview, string? parts, string? purpose) =>
        {
            var why = preview == true ? "preview" : purpose == "share" ? "share" : "download";
            var file = await s.ExportAsync(h.Current(), id, format ?? "pdf", why, parts);
            return preview == true ? Results.File(file.Content, file.ContentType) : Results.File(file.Content, file.ContentType, file.FileName);
        });
        // A new, unsent email (.eml) with the file attached, to open in the person's own email program.
        api.MapGet("/items/{id:guid}/export/draft", async (HttpContext h, ExportService s, Guid id, string? format, string? parts) =>
        {
            var file = await s.DraftAsync(h.Current(), id, format ?? "pdf", parts);
            return Results.File(file.Content, file.ContentType, file.FileName);
        });
        api.MapPost("/items/{id:guid}/export/email", async (HttpContext h, ExportService s, Guid id, EmailBody b, CancellationToken ct) =>
            new { message = await s.EmailAsync(h.Current(), id, b.Format ?? "pdf", b.To ?? "", b.Note, ct, b.Parts) });
        api.MapPost("/items/{id:guid}/export/check", async (HttpContext h, ExportService s, Guid id, CancellationToken ct) =>
        {
            var f = await h.Request.ReadFormAsync(ct);
            var file = f.Files["file"] ?? throw new DomainException("No file was received.");
            await using var stream = file.OpenReadStream();
            return await s.CheckAsync(h.Current(), id, stream);
        }).DisableAntiforgery();

        api.MapGet("/items/{id:guid}/audit", (HttpContext h, MeetingService s, Guid id) => s.GetAuditAsync(h.Current(), id));
        api.MapGet("/audit/verify", (HttpContext h, MeetingService s) => s.VerifyAuditAsync(h.Current()));
    }
}
