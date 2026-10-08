using VerbaFlow.Core.Providers;
using VerbaFlow.Core.Transcripts;
using VerbaFlow.Infrastructure.Azure;

namespace VerbaFlow.Infrastructure.Services;

/// <summary>
/// Combines the speech service's words with the diarizer's speaker turns. Each word goes to the speaker who was talking
/// while it was said, so a phrase that Azure attributed to one person is split where the voice changes.
/// </summary>
public static class SpeakerAligner
{
    /// <summary>A word further than this from any turn is given to whoever spoke just before it.</summary>
    public const int NearestTurnMs = 1500;

    public static TranscriptionResult Relabel(TranscriptionResult result, IReadOnlyList<SpeakerTurn> turns)
    {
        if (turns.Count == 0 || result.Segments.Count == 0) return result;
        var ordered = turns.OrderBy(t => t.StartMs).ToList();

        // 1. Cut every segment into runs of words said by the same diarizer speaker.
        var pieces = new List<(Segment Seg, int Who)>();
        foreach (var seg in result.Segments.OrderBy(s => s.StartMs))
        {
            var tokens = Tokens(seg);
            if (tokens is null) { pieces.Add((seg, SpeakerFor(seg.StartMs, seg.EndMs, ordered) ?? -1)); continue; }
            var who = tokens.Select(t => SpeakerFor(t.Start, t.End, ordered) ?? -1).ToList();
            Smooth(who);
            for (var i = 0; i < tokens.Count;)
            {
                var j = i;
                while (j + 1 < tokens.Count && who[j + 1] == who[i]) j++;
                var run = tokens.Skip(i).Take(j - i + 1).ToList();
                pieces.Add((seg with
                {
                    Id = Guid.NewGuid(), StartMs = run[0].Start, EndMs = run[^1].End,
                    Text = string.Join(" ", run.Select(t => t.Text)),
                    Words = seg.Words is null ? null : [.. run.Select(t => new WordTiming(t.Text, t.Start, t.End))],
                }, who[i]));
                i = j + 1;
            }
        }

        // 2. Pieces nobody could be matched to take the previous speaker (or the next one at the very start).
        var ids = pieces.Select(p => p.Who).ToList();
        for (var i = 0; i < ids.Count; i++) if (ids[i] < 0) ids[i] = i > 0 ? ids[i - 1] : ids.FirstOrDefault(x => x >= 0, 0);

        // 3. Number speakers in the order they first speak, then join neighbours into paragraphs.
        var speakers = new Dictionary<int, Speaker>();
        var relabelled = new List<Segment>();
        for (var i = 0; i < pieces.Count; i++)
        {
            if (!speakers.TryGetValue(ids[i], out var sp)) speakers[ids[i]] = sp = new Speaker(Guid.NewGuid(), $"Speaker {speakers.Count + 1}");
            relabelled.Add(pieces[i].Seg with { SpeakerId = sp.Id });
        }
        return result with { Speakers = speakers.Values.ToList(), Segments = AzureSpeechService.MergeTurns(relabelled) };
    }

    private sealed record Token(string Text, int Start, int End);

    /// <summary>The words of a segment, if their text matches the segment's text closely enough to split it safely.</summary>
    private static List<Token>? Tokens(Segment seg)
    {
        if (seg.Words is not { Count: > 1 } words) return null;
        static string Squash(string s) => new(s.Where(c => !char.IsWhiteSpace(c)).ToArray());
        if (Squash(string.Join("", words.Select(w => w.Text))) != Squash(seg.Text)) return null;
        return words.Select(w => new Token(w.Text, w.StartMs, w.EndMs)).ToList();
    }

    /// <summary>The diarizer speaker with the most overlap, else the nearest turn if it is close enough.</summary>
    private static int? SpeakerFor(int start, int end, List<SpeakerTurn> turns)
    {
        var best = turns.Select(t => (t.Speaker, Overlap: Math.Min(end, t.EndMs) - Math.Max(start, t.StartMs)))
            .Where(x => x.Overlap > 0).GroupBy(x => x.Speaker).Select(g => (Speaker: g.Key, Total: g.Sum(x => x.Overlap)))
            .OrderByDescending(x => x.Total).FirstOrDefault();
        if (best.Total > 0) return best.Speaker;
        var mid = (start + end) / 2;
        var near = turns.Select(t => (t.Speaker, Dist: mid < t.StartMs ? t.StartMs - mid : mid > t.EndMs ? mid - t.EndMs : 0))
            .OrderBy(x => x.Dist).First();
        return near.Dist <= NearestTurnMs ? near.Speaker : null;
    }

    /// <summary>A single word between two words of the same other speaker is almost always a boundary error, so it joins them.</summary>
    private static void Smooth(List<int> who)
    {
        for (var i = 1; i < who.Count - 1; i++)
            if (who[i - 1] == who[i + 1] && who[i] != who[i - 1] && who[i - 1] >= 0) who[i] = who[i - 1];
    }
}
