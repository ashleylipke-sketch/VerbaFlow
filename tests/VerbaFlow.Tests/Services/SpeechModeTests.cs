using VerbaFlow.Core.Domain;
using VerbaFlow.Core.Providers;
using VerbaFlow.Core.Transcripts;
using VerbaFlow.Infrastructure.Services;
using VerbaFlow.Infrastructure.StandIns;

namespace VerbaFlow.Tests.Services;

public class SpeechModeTests
{
    private const int BytesPerWord = StandInLiveSpeech.MsPerWord * 32; // 16 kHz, 16-bit

    /// <summary>What the live connection does with one connection's audio (see LiveEndpoints), without the socket.</summary>
    private static async Task SpeakAsync(LiveSessions sessions, LiveSession s, int fromMs, int words, bool end)
    {
        var (gen, _) = s.Connect(fromMs);
        await using var rec = await sessions.RecogniseAsync(s, fromMs, CancellationToken.None);
        for (var i = 0; i < words; i++) { rec.Write(new byte[BytesPerWord]); s.Heard(gen, BytesPerWord); }
        if (end) await rec.CompleteAsync(CancellationToken.None);
        else await rec.DisposeAsync();
        await foreach (var e in rec.Events.ReadAllAsync())
            if (e is { Kind: LiveEventKind.Final, Phrase: { } p })
                s.AddFinal(gen, p with { StartMs = p.StartMs + fromMs, EndMs = p.EndMs + fromMs, Words = p.Words?.Select(w => w with { StartMs = w.StartMs + fromMs, EndMs = w.EndMs + fromMs }).ToList() });
        if (end) s.Finish(gen);
    }

    private static RecordingUpload Speech(Env env, LiveUpload? live, int lengthMs) =>
        env.Dictation("Letter to Mr Smith") with { Mode = ItemMode.Speech, LengthMs = lengthMs, Live = live };

    [Fact]
    public async Task The_text_heard_live_becomes_the_items_text_with_spoken_commands_applied_and_the_speech_service_is_not_called_again()
    {
        using var env = new Env();
        var sessions = new LiveSessions(env.Stores, new StandInLiveSpeech(), env.Clock);
        var (s, _) = await sessions.StartAsync(env.Alice, "en-GB");
        await SpeakAsync(sessions, s, 0, 12, end: true); // "stand-in live text comma the speech service is not connected full stop"

        var shown = s.Snapshot().Text;
        Assert.Equal("stand-in live text, the speech service is not connected.", string.Join(" ", shown.Select(x => x.Text)));

        env.Speech.Fail = true; // the recording is not converted again
        var id = await env.Meeting.CreateRecordingAsync(env.Alice, Speech(env, new LiveUpload(sessions.Take(env.Alice, s.Id, Guid.Empty), 800, 2100), 12 * 400));
        await env.Processing.ProcessAsync(id);

        var item = await env.Item(id);
        Assert.Equal(ItemMode.Speech, item.Mode);
        Assert.Equal(ItemStatus.WithAuthor, item.Status);
        Assert.Equal("Speech to Text", (await env.Meeting.GetItemAsync(env.Alice, id)).Row.Status);
        var text = (await env.Stores.Transcripts.GetAsync(id))!.Render();
        Assert.Equal("stand-in live text, the speech service is not connected.", string.Join(" ", text.Select(t => t.Segment.Text)));
        Assert.All(text, t => Assert.Equal(env.Alice.Name, t.SpeakerName));
        Assert.All(text.SelectMany(t => t.Segment.Words!), w => Assert.True(w.EndMs <= 12 * 400)); // follow-along timings kept
        Assert.Null(env.Speech.LastOptions);
        var audit = await env.Stores.Audit.ListAsync(id);
        var live = audit.Single(a => a.Type == "speech.live").Details;
        Assert.Contains("\"liveText\":\"complete\"", live);
        Assert.Contains("\"textDelayMedianMs\":800", live);
        var done = audit.Last().Details;
        Assert.Contains("\"speechServiceForDictation\":\"live\"", done);
        Assert.Contains("\"liveText\":\"used (2 phrases)\"", done);
        Assert.Equal(0, env.Ai.Calls);
        Assert.Null(sessions.Take(env.Alice, s.Id, Guid.Empty)); // the session ended when it was saved
    }

    [Fact]
    public async Task A_lost_connection_sends_the_audio_again_from_the_last_finished_phrase_so_nothing_is_missed_or_doubled()
    {
        using var env = new Env();
        var sessions = new LiveSessions(env.Stores, new StandInLiveSpeech(), env.Clock);
        var (s, _) = await sessions.StartAsync(env.Alice, "en-GB");
        await SpeakAsync(sessions, s, 0, 11, end: false); // one phrase of 8 words finished, 3 words still being heard when the line dropped
        var resumeFrom = s.Snapshot().HeardToMs;
        Assert.Equal(8 * 400 - 50, resumeFrom);

        await SpeakAsync(sessions, s, resumeFrom, 5, end: true);
        var t = s.ToTranscript(Guid.Empty);
        Assert.Equal(1, t.Reconnects);
        Assert.True(t.Finished);
        Assert.Equal(resumeFrom + 5 * 400, t.CoveredMs);
        Assert.Equal(2, t.Phrases.Count);
        Assert.Equal(resumeFrom, t.Phrases[1].StartMs); // put back on the recording's clock
        Assert.Null(t.Problem(resumeFrom + 5 * 400));
        Assert.Equal("stand-in live text, the speech service is not connected. Each", string.Join(" ", s.Snapshot().Text.Select(x => x.Text)));
    }

    [Fact]
    public async Task Unfinished_or_missing_live_text_is_not_used_and_the_recording_is_converted_instead()
    {
        using var env = new Env();
        var sessions = new LiveSessions(env.Stores, new StandInLiveSpeech(), env.Clock);
        var (s, _) = await sessions.StartAsync(env.Alice, "en-GB");
        await SpeakAsync(sessions, s, 0, 8, end: false); // the connection went before the end
        env.Speech.Heard = "Dear Sir comma";

        var id = await env.Meeting.CreateRecordingAsync(env.Alice, Speech(env, new LiveUpload(sessions.Take(env.Alice, s.Id, Guid.Empty)), 8 * 400));
        await env.Processing.ProcessAsync(id);
        Assert.Equal("Dear Sir,", (await env.Stores.Transcripts.GetAsync(id))!.Render().Single().Segment.Text);
        Assert.Contains("not used: the live text was not finished", (await env.Stores.Audit.ListAsync(id)).Last().Details);

        // The live session had gone (the app was restarted, say): the recording is converted.
        var gone = await env.Meeting.CreateRecordingAsync(env.Alice, Speech(env, new LiveUpload(null), 5000));
        await env.Processing.ProcessAsync(gone);
        Assert.Equal("Dear Sir,", (await env.Stores.Transcripts.GetAsync(gone))!.Render().Single().Segment.Text);
        Assert.Contains("none (the live session had ended)", (await env.Stores.Audit.ListAsync(gone)).Single(a => a.Type == "speech.live").Details);

        // Live text that stops short of the end of the recording is not used either.
        Assert.NotNull(new LiveTranscript(Guid.Empty, "x", [new Segment(Guid.NewGuid(), Guid.NewGuid(), "en", 0, 900, "hi", 1, false)], true, 3000, 0, null).Problem(10_000));
    }

    [Fact]
    public async Task Running_the_conversion_again_works_from_the_recording_and_the_item_can_be_changed_like_a_dictation()
    {
        using var env = new Env();
        var sessions = new LiveSessions(env.Stores, new StandInLiveSpeech(), env.Clock);
        var (s, _) = await sessions.StartAsync(env.Alice, "en-GB");
        await SpeakAsync(sessions, s, 0, 8, end: true);
        var id = await env.Meeting.CreateRecordingAsync(env.Alice, Speech(env, new LiveUpload(sessions.Take(env.Alice, s.Id, Guid.Empty)), 8 * 400));
        await env.Processing.ProcessAsync(id);
        Assert.Contains("change-recording", (await env.Meeting.GetItemAsync(env.Alice, id)).Row.Actions);

        env.Speech.Heard = "Second try full stop";
        await env.Meeting.RerunAsync(env.Alice, id, null, dictationService: "fast");
        await env.Processing.ProcessAsync(id);
        Assert.Equal("Second try.", (await env.Stores.Transcripts.GetAsync(id))!.Render().Single().Segment.Text);
        Assert.NotNull(env.Speech.LastOptions);
    }

    [Fact]
    public async Task A_live_session_needs_a_chosen_language_and_belongs_to_the_person_who_started_it()
    {
        using var env = new Env();
        var sessions = new LiveSessions(env.Stores, new StandInLiveSpeech(), env.Clock);
        await Assert.ThrowsAsync<DomainException>(() => sessions.StartAsync(env.Alice, "auto"));
        await Assert.ThrowsAsync<DomainException>(() => sessions.StartAsync(env.Alice, "xh-ZA")); // isiXhosa cannot be transcribed yet
        var (s, ticket) = await sessions.StartAsync(env.Alice, "en-ZA,af-ZA");
        Assert.Equal(["en-ZA", "af-ZA"], s.Languages);

        Assert.Null(sessions.Redeem(Guid.NewGuid(), ticket));     // a ticket is for one session
        Assert.Same(s, sessions.Redeem(s.Id, sessions.NewTicket(env.Alice, s.Id)));
        var once = sessions.NewTicket(env.Alice, s.Id);
        Assert.NotNull(sessions.Redeem(s.Id, once));
        Assert.Null(sessions.Redeem(s.Id, once));                 // and works once
        var late = sessions.NewTicket(env.Alice, s.Id);
        env.Clock.Now = env.Clock.Now.AddMinutes(2);
        Assert.Null(sessions.Redeem(s.Id, late));                 // and only for a minute
        Assert.Throws<ForbiddenException>(() => sessions.NewTicket(env.Bob, s.Id));
        Assert.Null(sessions.Take(env.Bob, s.Id, Guid.Empty));
        Assert.NotNull(sessions.Take(env.Alice, s.Id, Guid.Empty));
    }

    [Fact]
    public async Task Speech_mode_items_are_listed_on_their_own_and_cannot_be_imported()
    {
        using var env = new Env();
        var speech = await env.Meeting.CreateRecordingAsync(env.Alice, Speech(env, new LiveUpload(null), 5000));
        await env.Meeting.CreateRecordingAsync(env.Alice, env.Dictation());
        Assert.Equal([speech], (await env.Meeting.ListAsync(env.Alice, new ListQuery(null, null, null, ItemMode.Speech))).Select(r => r.Id));
        await Assert.ThrowsAsync<DomainException>(() => env.Meeting.ImportAsync(env.Alice, env.Import("memo.m4a") with { Mode = ItemMode.Speech, Name = "Memo", DocumentType = "letter" }));
        await Assert.ThrowsAsync<DomainException>(() => env.Meeting.CreateRecordingAsync(env.Alice, Speech(env, null, 5000) with { DocumentType = null }));
    }
}
