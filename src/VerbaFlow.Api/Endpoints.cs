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
    /// <summary>"dictate" or "meeting" (the default when nothing is given).</summary>
    private static ItemMode ModeOf(string? mode) => string.IsNullOrWhiteSpace(mode) ? ItemMode.Meeting
        : mode.Trim().ToLowerInvariant() switch { "dictate" => ItemMode.Dictate, "meeting" => ItemMode.Meeting, _ => throw new DomainException("Unknown mode.") };

    public sealed record EmailBody(string? Format, string? To, string? Note, string? Parts = null);
    public sealed record NameBody(string Name);
    public sealed record TermBody(string Text, string? Note);
    public sealed record SavedTextBody(string? Code, string? Text);
    public sealed record LetterSpaceBody(int TopFirstMm, int TopNextMm, int BottomMm, int SideMm);
    public sealed record LinesBody(string Lines);

    /// <summary>The form of a save of an unfinished dictation: its audio file and fields.</summary>
    private static async Task<(IFormCollection Form, Stream Audio, IFormFile File)> DraftForm(HttpContext h, CancellationToken ct)
    {
        var f = await h.Request.ReadFormAsync(ct);
        var file = f.Files["audio"] ?? throw new DomainException("No audio was received.");
        return (f, file.OpenReadStream(), file);
    }

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
        // Saved text for "insert <code>" in a dictation: anyone signed in can read it, administrators change it.
        api.MapGet("/saved-text", (SavedTextService t) => t.ListAsync());
        api.MapPost("/saved-text", async (HttpContext h, SavedTextService t, SavedTextBody b) => Results.Ok(await t.AddAsync(h.Current(), b.Code, b.Text)));
        api.MapPut("/saved-text/{id:guid}", async (HttpContext h, SavedTextService t, Guid id, SavedTextBody b) => Results.Ok(await t.ChangeAsync(h.Current(), id, b.Code, b.Text)));
        api.MapDelete("/saved-text/{id:guid}", async (HttpContext h, SavedTextService t, Guid id) => { await t.RemoveAsync(h.Current(), id); return Results.NoContent(); });
        api.MapGet("/letterhead", (LetterheadService l) => l.GetAsync());
        // An administrator looks at the stored file to check it; it is the organisation's own letterhead, not customer content.
        api.MapGet("/letterhead/sample", async (LetterheadService l, string? format, bool? preview) =>
        {
            var word = format == "docx";
            var bytes = await l.SampleAsync(word ? LetterheadService.Word : LetterheadService.Pdf);
            return Results.File(bytes, word ? "application/vnd.openxmlformats-officedocument.wordprocessingml.document" : "application/pdf",
                preview == true ? null : word ? "sample letter.docx" : "sample letter.pdf");
        });
        api.MapGet("/letterhead/{kind}", async (HttpContext h, LetterheadService l, string kind) =>
        {
            if (!h.Current().IsAdmin) throw new ForbiddenException("Only administrators can open the letterhead file.");
            var k = kind == "pdf" ? LetterheadService.Pdf : LetterheadService.Word;
            var bytes = await l.ReadAsync(k) ?? throw new NotFoundException("There is no letterhead of that kind.");
            return Results.File(bytes, k == LetterheadService.Pdf ? "application/pdf" : "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                k == LetterheadService.Pdf ? "letterhead.pdf" : "letterhead.docx");
        });
        api.MapPost("/letterhead/{kind}", async (HttpContext h, LetterheadService l, string kind, IFormFile file) =>
        {
            await using var s = file.OpenReadStream();
            return Results.Ok(await l.UploadAsync(h.Current(), kind, s, file.FileName));
        }).DisableAntiforgery();
        api.MapDelete("/letterhead/{kind}", async (HttpContext h, LetterheadService l, string kind) => Results.Ok(await l.RemoveAsync(h.Current(), kind)));
        api.MapPut("/letterhead/space", async (HttpContext h, LetterheadService l, LetterSpaceBody b) =>
            Results.Ok(await l.SetPdfSpaceAsync(h.Current(), b.TopFirstMm, b.TopNextMm, b.BottomMm, b.SideMm)));

        api.MapGet("/items", (HttpContext h, MeetingService s, string? statuses, bool? selfAssigned, string? search, string? mode) =>
        {
            HashSet<ItemStatus>? set = null;
            if (!string.IsNullOrWhiteSpace(statuses))
                set = statuses.Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(x => Enum.Parse<ItemStatus>(x, true)).ToHashSet();
            ItemMode? m = string.IsNullOrWhiteSpace(mode) ? null : ModeOf(mode);
            return s.ListAsync(h.Current(), new ListQuery(set, selfAssigned, search, m));
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
                f["consentNoticeGiven"] == "true", markers, f["spokenLanguages"].ToString(), int.TryParse(f["numSpeakers"], out var ns1) ? ns1 : null,
                ModeOf(f["mode"]), f["clientReference"].ToString(), f["documentType"].ToString()), ct);
            return Results.Ok(new { id });
        }).DisableAntiforgery();

        // Unfinished dictations: save now, carry on later. Nothing is converted to text until finish.
        api.MapPost("/items/drafts", async (HttpContext h, MeetingService s, CancellationToken ct) =>
        {
            var (f, stream, file) = await DraftForm(h, ct);
            await using var _ = stream;
            var id = await s.SaveDraftAsync(h.Current(), null, new DraftUpload(stream, file.FileName, file.ContentType,
                int.TryParse(f["lengthMs"], out var l) ? l : 0, f["name"].ToString(),
                string.IsNullOrEmpty(f["language"]) ? "en" : f["language"].ToString(), f["spokenLanguages"].ToString(),
                f["clientReference"].ToString(), f["documentType"].ToString()), ct);
            return Results.Ok(new { id });
        }).DisableAntiforgery();
        api.MapPost("/items/{id:guid}/draft", async (HttpContext h, MeetingService s, Guid id, CancellationToken ct) =>
        {
            var (f, stream, file) = await DraftForm(h, ct);
            await using var _ = stream;
            await s.SaveDraftAsync(h.Current(), id, new DraftUpload(stream, file.FileName, file.ContentType, int.TryParse(f["lengthMs"], out var l) ? l : 0), ct);
            return Results.Ok(new { id });
        }).DisableAntiforgery();
        api.MapPost("/items/{id:guid}/draft/finish", async (HttpContext h, MeetingService s, Guid id, CancellationToken ct) =>
        {
            var (f, stream, file) = await DraftForm(h, ct);
            await using var _ = stream;
            await s.FinishDraftAsync(h.Current(), id, new DraftUpload(stream, file.FileName, file.ContentType, int.TryParse(f["lengthMs"], out var l) ? l : 0), ct);
            return Results.Ok(new { id });
        }).DisableAntiforgery();

        // The author changes a finished dictation's recording before it is approved; it is converted again.
        api.MapPost("/items/{id:guid}/recording", async (HttpContext h, MeetingService s, Guid id, CancellationToken ct) =>
        {
            var (f, stream, file) = await DraftForm(h, ct);
            await using var _ = stream;
            await s.ChangeRecordingAsync(h.Current(), id, new DraftUpload(stream, file.FileName, file.ContentType, int.TryParse(f["lengthMs"], out var l) ? l : 0),
                f["discardEdits"] == "true", ct);
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
                f["rightsConfirmed"] == "true", f["sourceNote"].ToString(), f["spokenLanguages"].ToString(), int.TryParse(f["numSpeakers"], out var ns2) ? ns2 : null,
                ModeOf(f["mode"]), f["clientReference"].ToString(), f["documentType"].ToString()), ct);
            return Results.Ok(new { id });
        }).DisableAntiforgery();

        api.MapPost("/items/{id:guid}/rerun", async (HttpContext h, MeetingService s, Guid id, RerunRequest? body) => { await s.RerunAsync(h.Current(), id, body?.NumSpeakers, body?.SpokenLanguages, body?.DiscardEdits ?? false, body?.Method, body?.Audio, body?.DictationService); return Results.NoContent(); });
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
