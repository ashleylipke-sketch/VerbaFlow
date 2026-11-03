using VerbaFlow.Core.Domain;
using VerbaFlow.Core.Providers;
using VerbaFlow.Core.Transcripts;
using VerbaFlow.Infrastructure.Services;
using VerbaFlow.Infrastructure.StandIns;

namespace VerbaFlow.Tests.Services;

/// <summary>Introductions at the start of an in-room meeting: naming speakers and checking voices against the samples.</summary>
public class IntroductionTests
{
    private static Segment Seg(Guid speaker, int start, string text, int wordMs = 400)
    {
        var words = text.Split(' ').Select((w, i) => new WordTiming(w, start + i * wordMs, start + (i + 1) * wordMs - 50)).ToList();
        return new Segment(Guid.NewGuid(), speaker, "en", start, words[^1].EndMs, text, 0.9, false, words);
    }

    private static readonly string[] People = ["Ann", "Ben"];
    private static readonly Introduction[] Intros = [new("Ann", 0, 2000), new("Ben", 2000, 4000)];

    [Fact]
    public void Each_speaker_is_named_after_the_introduction_they_spoke_most_of()
    {
        var a = new Speaker(Guid.NewGuid(), "Speaker 1"); var b = new Speaker(Guid.NewGuid(), "Speaker 2");
        var r = new TranscriptionResult("azure", [a, b], [Seg(b.Id, 0, "I am Ann."), Seg(a.Id, 2000, "I am Ben.")]);
        var named = IntroductionMatcher.NameSpeakers(r, Intros, People);
        Assert.Equal(0, named[b.Id]);
        Assert.Equal(1, named[a.Id]);
    }

    [Fact]
    public void One_speaker_who_spoke_in_both_introductions_gets_only_one_name()
    {
        var a = new Speaker(Guid.NewGuid(), "Speaker 1");
        var r = new TranscriptionResult("azure", [a], [Seg(a.Id, 0, "I am Ann and I am Ben too", 500)]);
        var named = IntroductionMatcher.NameSpeakers(r, Intros, People);
        Assert.Single(named);
    }

    [Fact]
    public void Boxes_are_cut_into_sentences_for_the_voice_check()
    {
        var a = new Speaker(Guid.NewGuid(), "Speaker 1");
        var r = new TranscriptionResult("azure", [a], [Seg(a.Id, 0, "We start now. Yes. Agreed by everyone here.")]);
        var pieces = IntroductionMatcher.Pieces(r);
        // "We start now." alone is too short to fingerprint, so "Yes." joins it.
        Assert.Equal([(0, 1550), (1600, 3150)], pieces.Select(p => (p.StartMs, p.EndMs)));
    }

    [Fact]
    public void A_sentence_that_clearly_sounds_like_someone_else_moves_to_them_and_a_close_call_stays()
    {
        var a = new Speaker(Guid.NewGuid(), "Speaker 1"); var b = new Speaker(Guid.NewGuid(), "Speaker 2");
        var r = new TranscriptionResult("azure", [a, b],
            [Seg(a.Id, 0, "I am Ann."), Seg(b.Id, 2000, "I am Ben."), Seg(a.Id, 5000, "This is Ben speaking now."), Seg(a.Id, 8000, "Hard to tell who said this.")]);
        var named = IntroductionMatcher.NameSpeakers(r, Intros, People);
        var pieces = IntroductionMatcher.Pieces(r);
        float[] ann = [1, 0], ben = [0, 1];
        var o = IntroductionMatcher.Decide(r, Intros, People, named, pieces,
            [ann, ben, [0.1f, 1f], [1f, 0.95f]], [ann, ben]);
        Assert.Equal(1, o.Moved);
        Assert.Equal(2, o.Checked); // the two introductions are not checked: they are the samples
        Assert.Equal([0, 1, 1, 0], o.Turns.Select(t => t.Speaker));
        var relabelled = SpeakerAligner.Relabel(r, o.Turns, o.Names);
        // Ben's introduction and the moved sentence are next to each other, so they join into one box.
        Assert.Equal(["Ann", "Ben", "Ann"], relabelled.Segments.Select(s => relabelled.Speakers.Single(x => x.Id == s.SpeakerId).Label));
        Assert.Equal("I am Ben. This is Ben speaking now.", relabelled.Segments[1].Text);
    }

    [Fact]
    public void A_voice_that_matched_no_introduction_keeps_its_own_label_unless_it_sounds_like_someone()
    {
        var a = new Speaker(Guid.NewGuid(), "Speaker 1"); var b = new Speaker(Guid.NewGuid(), "Speaker 2"); var c = new Speaker(Guid.NewGuid(), "Speaker 3");
        var r = new TranscriptionResult("azure", [a, b, c],
            [Seg(a.Id, 0, "I am Ann."), Seg(b.Id, 2000, "I am Ben."), Seg(c.Id, 5000, "A visitor says hello."), Seg(c.Id, 8000, "Sounds rather like Ben.")]);
        var named = IntroductionMatcher.NameSpeakers(r, Intros, People);
        var o = IntroductionMatcher.Decide(r, Intros, People, named, IntroductionMatcher.Pieces(r),
            [[1, 0], [0, 1], [-1, -0.2f], [0.1f, 1]], [[1, 0], [0, 1]]);
        Assert.Equal(1002, o.Turns[2].Speaker);
        Assert.Equal(1, o.Turns[3].Speaker);
    }

    // ---------- through the app ----------

    /// <summary>Stand-in voice model: passages starting on an even 4 s mark sound like Ann, odd ones like Ben, except the ones listed.</summary>
    private sealed class FakeVoices(params int[] benAt) : ISpeakerDiarizer, IVoicePrints
    {
        public string Name => "fake-voices";
        public Task<IReadOnlyList<SpeakerTurn>> DiarizeAsync(Stream audio, int? n, string? m, CancellationToken ct) => Task.FromResult<IReadOnlyList<SpeakerTurn>>([]);
        public Task<IReadOnlyList<float[]?>> PrintsAsync(Stream audio, IReadOnlyList<(int StartMs, int EndMs)> stretches, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<float[]?>>(stretches.Select(s => s.EndMs <= s.StartMs ? null
                : (s.StartMs / 4000 % 2 == 1 || benAt.Contains(s.StartMs / 4000)) ? new[] { 0f, 1f } : new[] { 1f, 0f }).ToList());
    }

    private static async Task<(Env env, Guid id)> Record(ISpeakerDiarizer? voices)
    {
        var env = new Env();
        var processing = new ProcessingService(env.Stores, env.Speech, env.Ai, new StandInAudioEnhancer(), env.Clock, env.Reporter, voices);
        var id = await env.Meeting.CreateRecordingAsync(env.Alice, env.Recording() with
        {
            Introductions = [new Introduction(" Ann ", 0, 3800), new Introduction("Ben", 4000, 7800)],
        });
        await processing.ProcessAsync(id);
        return (env, id);
    }

    [Fact]
    public async Task Introductions_set_the_number_of_speakers_and_name_them()
    {
        var (env, id) = await Record(null);
        using var _ = env;
        var item = await env.Stores.RequireItemAsync(id);
        Assert.Equal(2, item.NumSpeakers);
        Assert.Equal("azure", item.SpeakerMethod);
        Assert.Equal(["Ann", "Ben"], (await env.Meeting.GetItemAsync(env.Alice, id)).Introduced);
        var t = await env.Meeting.GetTranscriptAsync(env.Alice, id);
        Assert.Equal(["Ann", "Ben"], t.Speakers.Select(s => s.Label).Order());
        var done = (await env.Stores.Audit.ListAsync(id)).Single(a => a.Type == "processing.completed");
        Assert.Contains("2 people introduced; 2 speakers named from them; voice check not available", done.Details);
        Assert.DoesNotContain("Ann", done.Details); // counts only in the history, never names
    }

    [Fact]
    public async Task A_passage_that_sounds_like_the_other_person_is_moved_to_them()
    {
        var (env, id) = await Record(new FakeVoices(benAt: 2));
        using var _ = env;
        var t = await env.Meeting.GetTranscriptAsync(env.Alice, id);
        var third = t.Segments.Single(s => s.Text.Contains("passage 3"));
        Assert.Equal("Ben", t.Speakers.Single(s => s.Id == third.SpeakerId).Label);
        var done = (await env.Stores.Audit.ListAsync(id)).Single(a => a.Type == "processing.completed");
        Assert.Contains("voice check done", done.Details);
        Assert.Contains("1 moved to another person", done.Details);
    }

    [Fact]
    public async Task A_name_is_needed_for_every_introduction()
    {
        using var env = new Env();
        var ex = await Assert.ThrowsAsync<DomainException>(() => env.Meeting.CreateRecordingAsync(env.Alice, env.Recording() with
        {
            Introductions = [new Introduction("Ann", 0, 3800), new Introduction(" ", 4000, 7800)],
        }));
        Assert.Contains("name", ex.Message);
    }
}
