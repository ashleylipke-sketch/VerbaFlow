using VerbaFlow.Core.Providers;
using VerbaFlow.Core.Transcripts;
using VerbaFlow.Infrastructure.Azure;

namespace VerbaFlow.Infrastructure.Services;

/// <summary>
/// Keeps the speech service's speakers but splits a box where someone else clearly cut in. The speech service gives a
/// whole phrase to one person, so a quick interruption with no pause lands in the previous speaker's box. The local voice
/// model listens moment by moment: where it hears a different voice inside a box for long enough, that stretch becomes
/// its own box and goes to the speech-service speaker that voice belongs to (the one it overlaps most across the recording).
/// Unlike <see cref="SpeakerAligner"/>, it never renames or regroups speakers, so a weak local model can only add splits.
/// </summary>
public static class MixedBoxSplitter
{
    /// <summary>A different voice must last at least this long inside a box before the box is split.</summary>
    public const int MinRunMs = 700;

    public static (TranscriptionResult Result, int Split) Split(TranscriptionResult result, IReadOnlyList<SpeakerTurn> turns)
    {
        if (turns.Count == 0 || result.Segments.Count == 0) return (result, 0);
        var ordered = turns.OrderBy(t => t.StartMs).ToList();

        // Which speech-service speaker each local voice belongs to: the one it overlaps most across the whole recording.
        var owner = ordered.GroupBy(t => t.Speaker).ToDictionary(g => g.Key, g => result.Segments
            .Select(s => (s.SpeakerId, Overlap: g.Sum(t => Math.Max(0, Math.Min(s.EndMs, t.EndMs) - Math.Max(s.StartMs, t.StartMs)))))
            .GroupBy(x => x.SpeakerId).Select(x => (Id: x.Key, Total: x.Sum(y => y.Overlap)))
            .OrderByDescending(x => x.Total).First() is var best && best.Total > 0 ? best.Id : (Guid?)null);

        var split = 0;
        var output = new List<Segment>();
        foreach (var seg in result.Segments.OrderBy(s => s.StartMs))
        {
            var tokens = Tokens(seg);
            if (tokens is null) { output.Add(seg); continue; }
            var who = tokens.Select(t => SpeakerAligner.SpeakerFor(t.Start, t.End, ordered) is int local && owner.GetValueOrDefault(local) is Guid id ? id : seg.SpeakerId).ToList();
            for (var i = 1; i < who.Count - 1; i++) // a single stray word joins its neighbours
                if (who[i - 1] == who[i + 1] && who[i] != who[i - 1]) who[i] = who[i - 1];

            // Only a long enough stretch by someone else counts; shorter ones stay with the box's speaker.
            for (var i = 0; i < who.Count;)
            {
                var j = i;
                while (j + 1 < who.Count && who[j + 1] == who[i]) j++;
                if (who[i] != seg.SpeakerId && (j == i || tokens[j].End - tokens[i].Start < MinRunMs))
                    for (var k = i; k <= j; k++) who[k] = seg.SpeakerId;
                i = j + 1;
            }
            if (who.All(w => w == seg.SpeakerId)) { output.Add(seg); continue; }

            split++;
            for (var i = 0; i < tokens.Count;)
            {
                var j = i;
                while (j + 1 < tokens.Count && who[j + 1] == who[i]) j++;
                var run = tokens.Skip(i).Take(j - i + 1).ToList();
                output.Add(seg with
                {
                    Id = Guid.NewGuid(), SpeakerId = who[i], StartMs = run[0].Start, EndMs = run[^1].End,
                    Text = string.Join(" ", run.Select(t => t.Text)),
                    Words = [.. run.Select(t => new WordTiming(t.Timed, t.Start, t.End))],
                });
                i = j + 1;
            }
        }
        return split == 0 ? (result, 0) : (result with { Segments = AzureSpeechService.MergeTurns(output) }, split);
    }

    private sealed record Token(string Text, string Timed, int Start, int End);

    /// <summary>
    /// The box's own words (with punctuation) paired with the timed words. Only when the counts match, so the split
    /// cannot move a word to the wrong side; otherwise the box is left as it is.
    /// </summary>
    private static List<Token>? Tokens(Segment seg)
    {
        if (seg.Words is not { Count: > 1 } words) return null;
        var shown = seg.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (shown.Length != words.Count) return null;
        return shown.Select((w, i) => new Token(w, words[i].Text, words[i].StartMs, words[i].EndMs)).ToList();
    }
}
