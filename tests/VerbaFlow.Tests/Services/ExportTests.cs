using DocumentFormat.OpenXml.Packaging;
using Microsoft.Data.Sqlite;
using VerbaFlow.Core.Domain;
using VerbaFlow.Core.Providers;
using VerbaFlow.Infrastructure.Export;
using VerbaFlow.Infrastructure.StandIns;

namespace VerbaFlow.Tests.Services;

public class ExportTests
{
    private static string WordText(byte[] docx)
    {
        using var doc = WordprocessingDocument.Open(new MemoryStream(docx), false);
        return doc.MainDocumentPart!.Document!.Body!.InnerText;
    }

    [Fact]
    public async Task The_author_exports_a_draft_as_Word_with_the_transcript_summary_and_a_draft_marker_and_the_fingerprint_is_recorded()
    {
        using var env = new Env();
        var id = await env.ProcessedRecordingAsync();
        var exports = new ExportService(env.Stores, env.Clock);

        var file = await exports.ExportAsync(env.Alice, id, "docx");
        Assert.EndsWith("(draft).docx", file.FileName);
        using (var check = WordprocessingDocument.Open(new MemoryStream(file.Content), false))
            Assert.Empty(new DocumentFormat.OpenXml.Validation.OpenXmlValidator().Validate(check)); // Word opens it without complaint
        var text = WordText(file.Content);
        Assert.Contains("DRAFT", text);
        Assert.Contains("Not approved yet", text);
        Assert.Contains("Summary for", text);
        Assert.Contains("A: do it", text);
        var transcript = (await env.Stores.Transcripts.GetAsync(id))!.Render();
        Assert.Contains(transcript[0].Segment.Text, text);
        Assert.Contains(transcript[0].SpeakerName + ": ", text);

        var record = Assert.Single(await env.Stores.Exports.ListAsync());
        Assert.Equal("Draft", record.Marker);
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(file.Content)).ToLowerInvariant(), record.Sha256);
        var ev = (await env.Stores.Audit.ListAsync(id)).Last();
        Assert.Equal("item.exported", ev.Type);
        Assert.Contains(record.Sha256, ev.Details);
        Assert.True((await env.Stores.Audit.VerifyAsync()).Ok);
    }

    [Fact]
    public async Task An_approved_meeting_exports_as_a_PDF_marked_approved()
    {
        using var env = new Env();
        var id = await env.ProcessedRecordingAsync();
        await env.Meeting.ApproveAsync(env.Alice, id);
        var file = await new ExportService(env.Stores, env.Clock).ExportAsync(env.Carol, id, "pdf");
        Assert.Equal("application/pdf", file.ContentType);
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(file.Content, 0, 4));
        Assert.EndsWith("(approved).pdf", file.FileName);
        Assert.Equal("Approved", Assert.Single(await env.Stores.Exports.ListAsync()).Marker);
    }

    [Fact]
    public async Task A_copy_is_recognised_and_a_changed_copy_or_another_meetings_file_is_not()
    {
        using var env = new Env();
        var id = await env.ProcessedRecordingAsync();
        var other = await env.ProcessedRecordingAsync();
        var exports = new ExportService(env.Stores, env.Clock);
        var file = await exports.ExportAsync(env.Alice, id, "pdf");
        var otherFile = await exports.ExportAsync(env.Alice, other, "pdf");

        var ok = await exports.CheckAsync(env.Alice, id, new MemoryStream(file.Content));
        Assert.True(ok.Matches);
        Assert.Contains("unchanged", ok.Message);
        Assert.Equal("Draft", ok.Marker);

        var changed = (byte[])file.Content.Clone();
        changed[^20] ^= 1;
        Assert.False((await exports.CheckAsync(env.Alice, id, new MemoryStream(changed))).Matches);
        Assert.False((await exports.CheckAsync(env.Alice, id, new MemoryStream(otherFile.Content))).Matches);
        Assert.Equal("export.checked", (await env.Stores.Audit.ListAsync(id)).Last().Type);
    }

    [Fact]
    public async Task After_a_reopen_the_old_version_exports_as_superseded_and_its_files_still_check_against_the_new_version()
    {
        using var env = new Env();
        var id = await env.ProcessedRecordingAsync();
        await env.Meeting.ApproveAsync(env.Alice, id);
        var exports = new ExportService(env.Stores, env.Clock);
        var approved = await exports.ExportAsync(env.Alice, id, "docx");
        var req = await env.Meeting.RequestReopenAsync(env.Alice, id, "Typo");
        await env.Meeting.ApproveReopenAsync(env.Carol, req);
        var v2 = (await env.Meeting.ApproveReopenAsync(env.Dave, req))!.Value;

        Assert.Contains("SUPERSEDED", WordText((await exports.ExportAsync(env.Alice, id, "docx")).Content));
        Assert.True((await exports.CheckAsync(env.Alice, v2, new MemoryStream(approved.Content))).Matches);
    }

    [Fact]
    public async Task Only_the_owner_an_admin_or_someone_granted_export_may_export()
    {
        using var env = new Env();
        var id = await env.ProcessedRecordingAsync();
        var exports = new ExportService(env.Stores, env.Clock);
        await Assert.ThrowsAsync<ForbiddenException>(() => exports.ExportAsync(env.Bob, id, "pdf"));
        await env.Meeting.AssignAsync(env.Alice, id, env.Bob.Id, null);
        await Assert.ThrowsAsync<ForbiddenException>(() => exports.ExportAsync(env.Bob, id, "pdf")); // the assignee reads but does not export
        await Assert.ThrowsAsync<DomainException>(() => exports.ExportAsync(env.Alice, id, "rtf"));
        Assert.True((await env.Meeting.GetItemAsync(env.Alice, id)).CanExport);
        Assert.False((await env.Meeting.GetItemAsync(env.Bob, id)).CanExport);
        await exports.ExportAsync(env.Dave, id, "word");
    }

    [Fact]
    public async Task Recorded_fingerprints_can_never_be_changed_or_removed()
    {
        using var env = new Env();
        var id = await env.ProcessedRecordingAsync();
        await new ExportService(env.Stores, env.Clock).ExportAsync(env.Alice, id, "pdf");
        Assert.Throws<SqliteException>(() => env.Execute("UPDATE doc_exports SET json = json_set(json, '$.sha256', 'x')"));
        Assert.Throws<SqliteException>(() => env.Execute("DELETE FROM doc_exports"));
    }

    private sealed class BrokenMail : IEmailSender
    {
        public string Name => "broken";
        public Task<string> SendAsync(EmailMessage message, CancellationToken ct) =>
            throw new ProviderException(FaultKind.Settings, "401 Unauthorized: secret-key-detail");
    }

    [Fact]
    public async Task Emailing_attaches_the_exported_file_and_records_who_it_went_to()
    {
        using var env = new Env();
        var id = await env.ProcessedRecordingAsync();
        var outbox = Path.Combine(env.Dir, "outbox");
        var exports = new ExportService(env.Stores, env.Clock, new StandInEmailSender(outbox), env.Reporter);

        var message = await exports.EmailAsync(env.Alice, id, "pdf", "board@example.com; chair@example.org", "Please read before Friday.");
        Assert.Equal("Sent to board@example.com, chair@example.org.", message);
        var eml = File.ReadAllText(Assert.Single(Directory.GetFiles(outbox, "*.eml")));
        Assert.Contains("To: board@example.com, chair@example.org", eml);
        Assert.Contains("application/pdf", eml);
        Assert.Contains("filename=\"Board meeting (draft).pdf\"", eml);
        Assert.Contains("Reply-To: " + env.Alice.Email, eml);

        var record = Assert.Single(await env.Stores.Exports.ListAsync());
        var ev = (await env.Stores.Audit.ListAsync(id)).Last();
        Assert.Equal("item.emailed", ev.Type);
        Assert.Contains("board@example.com", ev.Details);
        Assert.Contains(record.Sha256, ev.Details);
        Assert.Contains("email", (await env.Stores.Audit.ListAsync(id)).Single(e => e.Type == "item.exported").Details);
        Assert.True((await env.Stores.Audit.VerifyAsync()).Ok);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-an-address")]
    [InlineData("Board <board@example.com>")]
    [InlineData("a@b.com,c@d.com,e@f.com,g@h.com,i@j.com,k@l.com,m@n.com,o@p.com,q@r.com,s@t.com,u@v.com")]
    public async Task Bad_or_too_many_addresses_send_nothing(string to)
    {
        using var env = new Env();
        var id = await env.ProcessedRecordingAsync();
        var outbox = Path.Combine(env.Dir, "outbox");
        var exports = new ExportService(env.Stores, env.Clock, new StandInEmailSender(outbox), env.Reporter);
        await Assert.ThrowsAsync<DomainException>(() => exports.EmailAsync(env.Alice, id, "pdf", to, null));
        Assert.False(Directory.Exists(outbox));
        Assert.Empty(await env.Stores.Exports.ListAsync());
    }

    [Fact]
    public async Task Someone_who_may_not_export_may_not_email_either()
    {
        using var env = new Env();
        var id = await env.ProcessedRecordingAsync();
        var exports = new ExportService(env.Stores, env.Clock, new StandInEmailSender(Path.Combine(env.Dir, "outbox")), env.Reporter);
        await Assert.ThrowsAsync<ForbiddenException>(() => exports.EmailAsync(env.Bob, id, "pdf", "x@example.com", null));
    }

    [Fact]
    public async Task A_failed_send_shows_a_plain_message_with_a_reference_and_keeps_the_detail_for_support()
    {
        using var env = new Env();
        var id = await env.ProcessedRecordingAsync();
        var exports = new ExportService(env.Stores, env.Clock, new BrokenMail(), env.Reporter);
        var ex = await Assert.ThrowsAsync<DomainException>(() => exports.EmailAsync(env.Alice, id, "docx", "board@example.com", null));
        Assert.Matches("VF-[A-Z0-9]{6}", ex.Message);
        Assert.DoesNotContain("secret-key-detail", ex.Message);
        Assert.DoesNotContain("401", ex.Message);
        Assert.Contains("secret-key-detail", Assert.Single(await env.Stores.SupportErrors.ListAsync()).Detail);
        Assert.Equal("email.failed", (await env.Stores.Audit.ListAsync(id)).Last().Type);
    }

    [Fact]
    public async Task A_preview_is_recorded_as_a_preview()
    {
        using var env = new Env();
        var id = await env.ProcessedRecordingAsync();
        await new ExportService(env.Stores, env.Clock).ExportAsync(env.Alice, id, "pdf", "preview");
        Assert.Contains("preview", (await env.Stores.Audit.ListAsync(id)).Last().Details);
    }

    [Fact]
    public async Task Only_the_chosen_parts_go_into_the_file_and_the_choice_is_recorded()
    {
        using var env = new Env();
        var id = await env.ProcessedRecordingAsync();
        var exports = new ExportService(env.Stores, env.Clock);
        var transcript = (await env.Stores.Transcripts.GetAsync(id))!.Render();

        var text = WordText((await exports.ExportAsync(env.Alice, id, "docx", parts: "summary,actions")).Content);
        Assert.Contains("Summary for", text);
        Assert.Contains("A: do it", text);
        Assert.Contains("ContainsSummary, Action points", text);
        Assert.DoesNotContain(transcript[0].Segment.Text, text);
        Assert.DoesNotContain("Transcript", text);
        Assert.Equal("summary,actions", Assert.Single(await env.Stores.Exports.ListAsync()).Parts);
        Assert.Contains("summary,actions", (await env.Stores.Audit.ListAsync(id)).Last().Details);

        var onlyTranscript = WordText((await exports.ExportAsync(env.Alice, id, "docx", parts: "transcript")).Content);
        Assert.Contains(transcript[0].Segment.Text, onlyTranscript);
        Assert.DoesNotContain("Summary for", onlyTranscript);

        await Assert.ThrowsAsync<DomainException>(() => exports.ExportAsync(env.Alice, id, "pdf", parts: ""));
        await Assert.ThrowsAsync<DomainException>(() => exports.ExportAsync(env.Alice, id, "pdf", parts: "everything"));
        Assert.DoesNotContain("Contains", WordText((await exports.ExportAsync(env.Alice, id, "docx")).Content));
    }
}
