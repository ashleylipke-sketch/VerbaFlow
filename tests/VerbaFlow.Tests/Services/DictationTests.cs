using VerbaFlow.Core.Domain;
using VerbaFlow.Infrastructure.Export;
using VerbaFlow.Infrastructure.Services;

namespace VerbaFlow.Tests.Services;

public class DictationTests
{
    [Fact]
    public async Task A_dictation_needs_no_consent_notice_is_heard_as_one_voice_and_its_spoken_commands_are_applied()
    {
        using var env = new Env();
        env.Speech.Heard = "Dear Ms Smith comma new paragraph. Thank you for your letter full stop";
        var id = await env.Meeting.CreateRecordingAsync(env.Alice, env.Dictation());
        await env.Processing.ProcessAsync(id);

        Assert.False(env.Speech.LastOptions!.Diarize);
        var item = await env.Item(id);
        Assert.Equal(ItemMode.Dictate, item.Mode);
        Assert.Empty(item.Markers);
        Assert.Equal(ItemStatus.WithAuthor, item.Status);
        var text = (await env.Stores.Transcripts.GetAsync(id))!.Render();
        Assert.Equal(["Dear Ms Smith,", "Thank you for your letter."], text.Select(t => t.Segment.Text));
        Assert.All(text, t => Assert.Equal(env.Alice.Name, t.SpeakerName));
        Assert.Equal(0, env.Ai.Calls); // no summary or minutes for a dictation
        Assert.Contains("\"dictationCommands\":3", (await env.Stores.Audit.ListAsync(id)).Last().Details);
    }

    [Fact]
    public async Task Dictations_and_meetings_are_listed_separately()
    {
        using var env = new Env();
        var dictation = await env.Meeting.CreateRecordingAsync(env.Alice, env.Dictation());
        var meeting = await env.Meeting.CreateRecordingAsync(env.Alice, env.Recording());
        Assert.Equal([dictation], (await env.Meeting.ListAsync(env.Alice, new ListQuery(null, null, null, ItemMode.Dictate))).Select(r => r.Id));
        Assert.Equal([meeting], (await env.Meeting.ListAsync(env.Alice, new ListQuery(null, null, null, ItemMode.Meeting))).Select(r => r.Id));
        Assert.Equal("Dictate", (await env.Meeting.GetItemAsync(env.Alice, dictation)).Mode);
    }

    [Fact]
    public async Task A_dictation_goes_to_a_typist_and_is_approved_like_a_meeting_and_survives_a_reopen()
    {
        using var env = new Env();
        var id = await env.Meeting.CreateRecordingAsync(env.Alice, env.Dictation());
        await env.Processing.ProcessAsync(id);
        await env.Meeting.AssignAsync(env.Alice, id, env.Bob.Id, null);
        await env.Meeting.AcceptAsync(env.Bob, id);
        Assert.Equal(ItemStatus.WithAssignee, (await env.Item(id)).Status);
        await env.Meeting.ReturnAsync(env.Bob, id);
        await env.Meeting.ApproveAsync(env.Alice, id);
        var req = await env.Meeting.RequestReopenAsync(env.Alice, id, "Typo");
        await env.Meeting.ApproveReopenAsync(env.Carol, req);
        var v2 = (await env.Meeting.ApproveReopenAsync(env.Dave, req))!.Value;
        Assert.Equal(ItemMode.Dictate, (await env.Item(v2)).Mode);
    }

    [Fact]
    public async Task A_dictation_exports_as_its_text_only_with_no_times_or_speaker_names()
    {
        using var env = new Env();
        env.Speech.Heard = "Please call me back full stop";
        var id = await env.Meeting.CreateRecordingAsync(env.Alice, env.Dictation());
        await env.Processing.ProcessAsync(id);
        var file = await new ExportService(env.Stores, env.Clock).ExportAsync(env.Alice, id, "docx", parts: "summary,minutes");
        using var doc = DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Open(new MemoryStream(file.Content), false);
        var text = doc.MainDocumentPart!.Document!.Body!.InnerText;
        Assert.Contains("LetterPlease call me back.", text);
        Assert.DoesNotContain(env.Alice.Name + ":", text);
        Assert.DoesNotContain("0:00", text);
        Assert.DoesNotContain("Contains", text);
        var draft = await new ExportService(env.Stores, env.Clock).DraftAsync(env.Alice, id, "pdf");
        Assert.Contains(Convert.ToBase64String("Dictation: Letter to the client (DRAFT)"u8.ToArray()), System.Text.Encoding.ASCII.GetString(draft.Content));
    }

    [Fact]
    public async Task An_imported_dictation_is_the_authors_own_and_needs_no_speaker_count()
    {
        using var env = new Env();
        var id = await env.Meeting.ImportAsync(env.Alice, env.Import("memo.m4a") with { Mode = ItemMode.Dictate, NumSpeakers = 3, Name = "Memo", DocumentType = "email" });
        var item = await env.Item(id);
        Assert.Equal(ItemStatus.WithAuthor, item.Status);
        Assert.Null(item.NumSpeakers);
        Assert.Equal("With Author", (await env.Meeting.GetItemAsync(env.Alice, id)).Row.Status);
    }

    [Fact]
    public async Task A_client_reference_given_when_recording_or_importing_is_kept_on_the_item()
    {
        using var env = new Env();
        var rec = await env.Meeting.CreateRecordingAsync(env.Alice, env.Dictation() with { ClientReference = " ACME-042 " });
        var imp = await env.Meeting.ImportAsync(env.Alice, env.Import() with { ClientReference = "B-7" });
        Assert.Equal("ACME-042", (await env.Item(rec)).ClientReference);
        Assert.Equal("B-7", (await env.Item(imp)).ClientReference);
    }

    [Fact]
    public async Task A_dictation_needs_a_name_and_a_kind_of_document_and_keeps_the_kind()
    {
        using var env = new Env();
        await Assert.ThrowsAsync<DomainException>(() => env.Meeting.CreateRecordingAsync(env.Alice, env.Dictation(" ") with { ClientReference = "ACME-042" }));
        await Assert.ThrowsAsync<DomainException>(() => env.Meeting.CreateRecordingAsync(env.Alice, env.Dictation() with { DocumentType = null }));
        await Assert.ThrowsAsync<DomainException>(() => env.Meeting.CreateRecordingAsync(env.Alice, env.Dictation() with { DocumentType = "fax" }));
        await Assert.ThrowsAsync<DomainException>(() => env.Meeting.ImportAsync(env.Alice, env.Import("memo.m4a") with { Mode = ItemMode.Dictate, DocumentType = "letter" }));
        var id = await env.Meeting.CreateRecordingAsync(env.Alice, env.Dictation() with { DocumentType = "Email" });
        Assert.Equal("email", (await env.Meeting.GetItemAsync(env.Alice, id)).DocumentType);
        // A meeting needs neither.
        await env.Meeting.ImportAsync(env.Alice, env.Import());
    }

    [Fact]
    public async Task Paragraph_breaks_reach_the_item_page()
    {
        using var env = new Env();
        env.Speech.Heard = "First part full stop new paragraph Second part full stop new paragraph Third part";
        var id = await env.Meeting.CreateRecordingAsync(env.Alice, env.Dictation());
        await env.Processing.ProcessAsync(id);
        var t = await env.Meeting.GetTranscriptAsync(env.Alice, id);
        Assert.Equal(["First part.", "Second part.", "Third part"], t.Segments.Select(s => s.Text));
        Assert.Equal([true, true, true], t.Segments.Select(s => s.NewParagraph == true));
    }

    [Fact]
    public async Task Pressing_enter_while_editing_starts_a_new_paragraph_without_changing_the_words()
    {
        using var env = new Env();
        env.Speech.Heard = "Dear Ms Smith comma thank you full stop We agree full stop";
        var id = await env.Meeting.CreateRecordingAsync(env.Alice, env.Dictation());
        await env.Processing.ProcessAsync(id);
        var seg = (await env.Meeting.GetTranscriptAsync(env.Alice, id)).Segments.Single();
        await env.Meeting.EditSegmentAsync(env.Alice, id, seg.Id, "Dear Ms Smith, thank you.\r\n\r\n  We agree.  ");
        var after = (await env.Meeting.GetTranscriptAsync(env.Alice, id)).Segments.Single();
        Assert.Equal("Dear Ms Smith, thank you.\nWe agree.", after.Text);
        Assert.Equal(seg.Words!.Select(w => w.StartMs), after.Words!.Select(w => w.StartMs));
        var file = await new ExportService(env.Stores, env.Clock).ExportAsync(env.Alice, id, "docx");
        using var doc = DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Open(new MemoryStream(file.Content), false);
        var paras = doc.MainDocumentPart!.Document!.Body!.Descendants<DocumentFormat.OpenXml.Wordprocessing.Paragraph>().Select(p => p.InnerText).ToList();
        Assert.Contains("Dear Ms Smith, thank you.", paras);
        Assert.Contains("We agree.", paras);
    }

    [Fact]
    public void A_line_break_typed_at_the_start_makes_a_passage_begin_a_paragraph()
    {
        var a = new VerbaFlow.Core.Transcripts.Segment(Guid.NewGuid(), Guid.NewGuid(), "en", 0, 1000, "One.", 0.9, false, null, true);
        var b = a with { Id = Guid.NewGuid(), StartMs = 1000, Text = "Two.", NewParagraph = false };
        var t = VerbaFlow.Core.Transcripts.Transcript.CreateMachineV1(Guid.NewGuid(), "x", [], [a, b], DateTimeOffset.UnixEpoch);
        t.EditSegment(b.Id, "\nTwo.", Guid.NewGuid(), "author", DateTimeOffset.UnixEpoch);
        Assert.Equal([true, true], t.Render().Select(r => r.Segment.NewParagraph == true));
        Assert.Equal("Two.", t.Render()[1].Segment.Text);
    }
}
