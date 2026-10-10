using System.Diagnostics;
using VerbaFlow.Core.Providers;
using VerbaFlow.Core.Transcripts;
using VerbaFlow.Infrastructure.Services;

namespace VerbaFlow.Tests.Services;

public class LanguagePerPieceTests
{
    private static Segment Seg(Guid speaker, int start, int end, string text, string lang = "en", params (string, int, int)[] words) =>
        new(Guid.NewGuid(), speaker, lang, start, end, text, 0.9, false,
            words.Length == 0 ? null : words.Select(w => new WordTiming(w.Item1, w.Item2, w.Item3)).ToList());

    [Fact]
    public void Pieces_cover_the_whole_recording_end_at_pauses_and_respect_the_limits()
    {
        var s = Guid.NewGuid();
        var heard = new[]
        {
            Seg(s, 500, 2000, "a b", "en", ("a", 500, 1000), ("b", 1100, 2000)),
            Seg(s, 2600, 4000, "c", "en", ("c", 2600, 4000)),          // pause 2000-2600: too early (piece < 3 s)
            Seg(s, 5000, 6000, "d", "en", ("d", 5000, 6000)),          // pause 4000-5000: cut at 4500
            Seg(s, 6100, 30000, "e", "en", ("e", 6100, 30000)),        // no pause for 24 s: hard cut at 15 s
        };
        var pieces = LanguagePerPieceSpeech.Pieces(heard, 31000);
        Assert.Equal(0, pieces[0].Start);
        Assert.Equal(4500, pieces[0].End);
        Assert.Equal(31000, pieces[^1].End);
        for (var i = 1; i < pieces.Count; i++) Assert.Equal(pieces[i - 1].End, pieces[i].Start); // no gaps, nothing skipped
        Assert.All(pieces, p => Assert.InRange(p.End - p.Start, 1, LanguagePerPieceSpeech.MaxPieceMs + LanguagePerPieceSpeech.MinPieceMs));
    }

    [Fact]
    public void A_sentence_is_not_cut_at_a_short_pause_when_it_can_end_where_the_sentence_ends()
    {
        var s = Guid.NewGuid();
        var heard = new[]
        {
            // "Thanks, and finally, where do you ... live at the moment?" with a 500 ms hesitation inside
            Seg(s, 0, 6000, "q", "en", ("where", 0, 3000), ("do", 3100, 3500), ("live", 4000, 6000)),
            Seg(s, 7000, 12000, "a", "af", ("ek", 7000, 12000)),
        };
        var pieces = LanguagePerPieceSpeech.Pieces(heard, 12000);
        Assert.Equal([(0, 6500), (6500, 12000)], pieces);
    }

    [Fact]
    public void Silence_with_no_words_is_still_covered()
    {
        var pieces = LanguagePerPieceSpeech.Pieces([], 40000);
        Assert.Equal([(0, 15000), (15000, 30000), (30000, 40000)], pieces);
    }

    /// <summary>The full pass gives two speakers; each piece answers in English or Afrikaans in turn.</summary>
    private sealed class FakeSpeech : ISpeechService
    {
        public List<TranscribeOptions> Calls { get; } = [];
        public readonly Speaker A = new(Guid.NewGuid(), "Speaker 1"), B = new(Guid.NewGuid(), "Speaker 2");
        public async Task<TranscriptionResult> TranscribeAsync(Stream audio, TranscribeOptions o, CancellationToken ct)
        {
            var ms = new MemoryStream(); await audio.CopyToAsync(ms, ct);
            Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(ms.ToArray(), 0, 4));
            Calls.Add(o);
            if (o.Diarize) // a full pass: speaker A for 0-5 s, B for 6-12 s, pause in between
                return new("fake", [A, B], [Seg(A.Id, 200, 5000, "hello there", "en", ("hello", 200, 2000), ("there", 2100, 5000)),
                                            Seg(B.Id, 6000, 12000, "goeie more", "en", ("goeie", 6000, 9000), ("more", 9100, 12000))]);
            var lang = Calls.Count(c => !c.Diarize) == 1 ? "en" : "af";
            var len = (int)((ms.Length - 44) / 32);
            return new("fake", [new Speaker(Guid.NewGuid(), "Speaker 1")], [Seg(Guid.NewGuid(), 100, len - 100, lang == "en" ? "Hi Lee-Anne" : "Goeie more", lang, ("w", 100, len - 100))]);
        }
    }

    [Fact]
    public async Task Each_piece_is_sent_with_every_language_and_the_speakers_come_from_the_full_pass()
    {
        if (!await FfmpegAudioEnhancer.IsAvailableAsync(null)) return;
        var wav = Path.Combine(Path.GetTempPath(), $"vf-pieces-{Guid.NewGuid():N}.wav");
        var p = Process.Start(new ProcessStartInfo("ffmpeg", ["-loglevel", "error", "-y", "-f", "lavfi", "-i", "sine=f=300:d=12", wav]) { RedirectStandardError = true })!;
        await p.WaitForExitAsync();
        var fake = new FakeSpeech();
        TranscriptionResult r;
        try
        {
            await using var f = File.OpenRead(wav);
            r = await new LanguagePerPieceSpeech(fake, null).TranscribeAsync(f, new(["en-ZA", "af-ZA"], 2, true), default);
        }
        finally { File.Delete(wav); }

        Assert.Equal(["en-ZA"], fake.Calls[0].CandidateLanguages);
        Assert.True(fake.Calls[0].Diarize);
        Assert.Equal(4, fake.Calls.Count); // full pass + pieces 0-5.5 s and 5.5-12 s + speaker pass in Afrikaans
        Assert.Equal(["af-ZA"], fake.Calls[3].CandidateLanguages); // most of it was Afrikaans (6.5 s against 5.5 s)
        Assert.True(fake.Calls[3].Diarize);
        Assert.All(fake.Calls.Skip(1).Take(2), c => { Assert.Equal(["en-ZA", "af-ZA"], c.CandidateLanguages); Assert.False(c.Diarize); });
        Assert.Equal(["en", "af"], r.Segments.Select(s => s.Language));
        Assert.Equal(["Hi Lee-Anne", "Goeie more"], r.Segments.Select(s => s.Text));
        Assert.Equal(5600, r.Segments[1].StartMs); // moved back to its place in the full recording (piece starts at 5.5 s)
        Assert.Equal(2, r.Speakers.Count);
        Assert.NotEqual(r.Segments[0].SpeakerId, r.Segments[1].SpeakerId);
        Assert.EndsWith("+language-per-piece", r.Engine);
        Assert.Equal("2 pieces; en in 1, af in 1; speakers worked out in af-ZA", r.Detail);
    }

    [Fact]
    public void The_main_language_is_the_chosen_one_heard_for_longest()
    {
        var s = Guid.NewGuid();
        Assert.Equal("af-ZA", LanguagePerPieceSpeech.MainLanguage([Seg(s, 0, 4000, "x", "en"), Seg(s, 4000, 9000, "y", "af"), Seg(s, 9000, 11000, "z", "af-ZA")], ["en-ZA", "af-ZA"]));
        Assert.Equal("en-ZA", LanguagePerPieceSpeech.MainLanguage([Seg(s, 0, 4000, "x", "en"), Seg(s, 4000, 5000, "y", "af")], ["en-ZA", "af-ZA"]));
        Assert.Null(LanguagePerPieceSpeech.MainLanguage([Seg(s, 0, 4000, "x", "de")], ["en-ZA", "af-ZA"]));
    }

    [Fact]
    public async Task One_language_goes_straight_through()
    {
        var fake = new FakeSpeech();
        await new LanguagePerPieceSpeech(fake, null).TranscribeAsync(LanguagePerPieceSpeech.Wav(new byte[3200], 0, 3200), new(["af-ZA"], 2, true), default);
        Assert.Single(fake.Calls);
    }

    /// <summary>Like Azure: refuses a piece longer than <c>mixedAbove</c> ms with "multiple languages identified".</summary>
    private sealed class MixedSpeech(int mixedAbove) : ISpeechService
    {
        public List<int> PieceLengths { get; } = [];
        private readonly Speaker a = new(Guid.NewGuid(), "Speaker 1");
        public async Task<TranscriptionResult> TranscribeAsync(Stream audio, TranscribeOptions o, CancellationToken ct)
        {
            var ms = new MemoryStream(); await audio.CopyToAsync(ms, ct);
            var len = (int)((ms.Length - 44) / 32);
            if (o.Diarize) // the full pass
                return new("fake", [a], [Seg(a.Id, 200, 5000, "first pass", "en", ("first", 200, 2000), ("pass", 2100, 5000))]);
            PieceLengths.Add(len);
            if (len > mixedAbove) throw new MixedLanguagesException("422 MultipleLanguagesIdentified");
            return new("fake", [a], [Seg(Guid.NewGuid(), 0, len, "piece", "af", ("piece", 0, len))]);
        }
    }

    private static async Task<(TranscriptionResult, MixedSpeech)> RunMixedAsync(int mixedAbove)
    {
        var fake = new MixedSpeech(mixedAbove);
        var pcm = new byte[6000 * 32]; // 6 s of silence
        var r = await new LanguagePerPieceSpeech(fake, null).TranscribeAsync(LanguagePerPieceSpeech.Wav(pcm, 0, pcm.Length), new(["en-ZA", "af-ZA"], 2, true), default);
        return (r, fake);
    }

    [Fact]
    public async Task A_piece_with_both_languages_is_cut_in_two_and_tried_again()
    {
        if (!await FfmpegAudioEnhancer.IsAvailableAsync(null)) return;
        var (r, fake) = await RunMixedAsync(4000);
        Assert.Equal([6000, 3000, 3000], fake.PieceLengths);
        Assert.Equal("piece piece", string.Join(" ", r.Segments.Select(s => s.Text)));
        Assert.Contains("1 cut again for mixed languages", r.Detail);
    }

    [Fact]
    public async Task A_piece_too_mixed_to_cut_keeps_what_the_first_pass_heard()
    {
        if (!await FfmpegAudioEnhancer.IsAvailableAsync(null)) return;
        var (r, _) = await RunMixedAsync(0);
        Assert.Equal("first pass", Assert.Single(r.Segments).Text);
        Assert.Contains("too mixed, kept from the first-language pass", r.Detail);
    }
}
