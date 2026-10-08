using VerbaFlow.Core.Domain;
using VerbaFlow.Core.Providers;
using VerbaFlow.Core.Transcripts;
using VerbaFlow.Infrastructure.Services;
using VerbaFlow.Infrastructure.StandIns;

namespace VerbaFlow.Tests.Services;

public class SpeakerSeparationTests
{
    private static Segment Seg(Guid speaker, int start, string text, int wordMs = 400) 
    {
        var words = text.Split(' ').Select((w, i) => new WordTiming(w, start + i * wordMs, start + (i + 1) * wordMs - 50)).ToList();
        return new Segment(Guid.NewGuid(), speaker, "en", start, words[^1].EndMs, text, 0.9, false, words);
    }

    private static TranscriptionResult AzureSaysOnePerson(params (int Start, string Text)[] phrases)
    {
        var s = new Speaker(Guid.NewGuid(), "Speaker 1");
        return new TranscriptionResult("azure", [s], phrases.Select(p => Seg(s.Id, p.Start, p.Text)).ToList());
    }

    [Fact]
    public void A_phrase_is_split_where_the_voice_changes_and_speakers_are_numbered_by_first_appearance()
    {
        // 8 words, 400 ms each: the first 4 are one voice, the last 4 another.
        var azure = AzureSaysOnePerson((0, "Good morning everyone welcome to the meeting today"));
        var turns = new[] { new SpeakerTurn(0, 1600, 7), new SpeakerTurn(1600, 3200, 3) };
        var r = SpeakerAligner.Relabel(azure, turns);
        Assert.Equal(2, r.Segments.Count);
        Assert.Equal("Good morning everyone welcome", r.Segments[0].Text);
        Assert.Equal("to the meeting today", r.Segments[1].Text);
        Assert.Equal(["Speaker 1", "Speaker 2"], r.Speakers.Select(s => s.Label));
        Assert.NotEqual(r.Segments[0].SpeakerId, r.Segments[1].SpeakerId);
        Assert.Equal(1600, r.Segments[1].StartMs);
        Assert.Equal(4, r.Segments[1].Words!.Count);
    }

    [Fact]
    public void Voices_azure_merged_are_separated_and_voices_azure_split_are_joined()
    {
        var a = new Speaker(Guid.NewGuid(), "Speaker 1"); var b = new Speaker(Guid.NewGuid(), "Speaker 2");
        // Azure thinks two different people spoke, the diarizer says it is one voice throughout.
        var azure = new TranscriptionResult("azure", [a, b], [Seg(a.Id, 0, "one two three"), Seg(b.Id, 1300, "four five six")]);
        var r = SpeakerAligner.Relabel(azure, [new SpeakerTurn(0, 3000, 0)]);
        Assert.Single(r.Speakers);
        Assert.Single(r.Segments);
        Assert.Equal("one two three four five six", r.Segments[0].Text);
    }

    [Fact]
    public void One_stray_word_inside_another_persons_turn_is_not_split_out()
    {
        var azure = AzureSaysOnePerson((0, "alpha bravo charlie delta echo"));
        // The diarizer's boundary clips the middle word into a different voice.
        var turns = new[] { new SpeakerTurn(0, 750, 1), new SpeakerTurn(800, 1150, 2), new SpeakerTurn(1200, 2000, 1) };
        var r = SpeakerAligner.Relabel(azure, turns);
        Assert.Single(r.Segments);
        Assert.Single(r.Speakers);
    }

    [Fact]
    public void Words_in_silence_between_turns_go_to_the_nearest_speaker_and_far_ones_to_the_previous()
    {
        var azure = AzureSaysOnePerson((0, "one two three"), (20000, "four five six"));
        var r = SpeakerAligner.Relabel(azure, [new SpeakerTurn(100, 1200, 4)]);
        Assert.All(r.Segments, s => Assert.Equal(r.Speakers[0].Id, s.SpeakerId));
        Assert.Single(r.Speakers);
    }

    [Fact]
    public void Without_turns_or_words_the_azure_result_is_kept()
    {
        var azure = AzureSaysOnePerson((0, "one two three"));
        Assert.Same(azure, SpeakerAligner.Relabel(azure, []));
        var s = new Speaker(Guid.NewGuid(), "Speaker 1");
        var noWords = new TranscriptionResult("azure", [s], [new Segment(Guid.NewGuid(), s.Id, "en", 0, 2000, "no word timings here", 0.9, false)]);
        var r = SpeakerAligner.Relabel(noWords, [new SpeakerTurn(0, 1000, 0), new SpeakerTurn(1000, 2000, 1)]);
        Assert.Single(r.Segments); // cannot be split safely, so it goes to the speaker with most overlap
    }

    [Fact]
    public void Text_that_does_not_match_its_word_list_is_not_split()
    {
        var s = new Speaker(Guid.NewGuid(), "Speaker 1");
        var words = new[] { new WordTiming("hello", 0, 400), new WordTiming("there", 400, 800) };
        var seg = new Segment(Guid.NewGuid(), s.Id, "en", 0, 800, "Something else entirely", 0.9, false, words);
        var r = SpeakerAligner.Relabel(new TranscriptionResult("azure", [s], [seg]), [new SpeakerTurn(0, 400, 0), new SpeakerTurn(400, 800, 1)]);
        Assert.Single(r.Segments);
    }

    // ---------- how the app uses it ----------

    private sealed class FakeDiarizer : ISpeakerDiarizer
    {
        public bool Fail { get; set; }
        public string Name => "fake-diarizer";
        public Task<IReadOnlyList<SpeakerTurn>> DiarizeAsync(Stream audio, CancellationToken ct) =>
            Fail ? throw new InvalidOperationException("model missing") : Task.FromResult<IReadOnlyList<SpeakerTurn>>([new SpeakerTurn(0, 1_000_000, 0)]);
    }

    private static async Task<(Env env, Guid id)> Run(ISpeakerDiarizer d)
    {
        var env = new Env();
        var processing = new ProcessingService(env.Stores, env.Speech, env.Ai, new StandInAudioEnhancer(), env.Clock, d);
        var id = await env.Meeting.CreateRecordingAsync(env.Alice, env.Recording());
        await processing.ProcessAsync(id);
        return (env, id);
    }

    [Fact]
    public async Task The_local_diarizer_decides_the_speakers_and_the_engine_says_so()
    {
        var (env, id) = await Run(new FakeDiarizer());
        using var _ = env;
        var t = await env.Meeting.GetTranscriptAsync(env.Alice, id);
        Assert.Single(t.Speakers);
        Assert.Contains("fake-diarizer", t.Engine);
        var done = (await env.Stores.Audit.ListAsync(id)).Single(a => a.Type == "processing.completed");
        Assert.Contains("fake-diarizer", done.Details);
    }

    [Fact]
    public async Task If_the_local_diarizer_fails_azures_speakers_are_kept_and_the_item_still_completes()
    {
        var (env, id) = await Run(new FakeDiarizer { Fail = true });
        using var _ = env;
        var t = await env.Meeting.GetTranscriptAsync(env.Alice, id);
        Assert.True(t.Speakers.Count > 1);
        var done = (await env.Stores.Audit.ListAsync(id)).Single(a => a.Type == "processing.completed");
        Assert.Contains("Local speaker separation failed", done.Details);
        Assert.Contains("azure", done.Details);
    }

    // ---------- the real model ----------

    [Fact]
    public async Task The_real_model_finds_all_four_voices_in_the_four_speaker_sample()
    {
        if (!await FfmpegAudioEnhancer.IsAvailableAsync(null)) return;
        var wav = Path.Combine(AppContext.BaseDirectory, "TestData", "four-speakers.wav");
        var folder = Path.Combine(Path.GetTempPath(), "verbaflow-test-models");
        using var http = new HttpClient();
        using var d = new SherpaSpeakerDiarizer(new DiarizationOptions(folder, null), http);
        try { await d.EnsureModelsAsync(); } catch (HttpRequestException) { return; } // offline machine
        await using var audio = File.OpenRead(wav);
        var turns = await d.DiarizeAsync(audio, default);
        Assert.Equal(4, turns.Select(t => t.Speaker).Distinct().Count());
        Assert.All(turns, t => Assert.True(t.EndMs > t.StartMs));
    }
}
