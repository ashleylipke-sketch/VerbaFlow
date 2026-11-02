using VerbaFlow.Core.Providers;
using VerbaFlow.Core.Transcripts;
using VerbaFlow.Infrastructure.Azure;

namespace VerbaFlow.Infrastructure.Services;

/// <summary>
/// Puts an online meeting's transcript together from each person's own audio: every person's words were transcribed from
/// their own microphone, so who said what is known for certain. The words are moved onto the meeting's clock and put in
/// time order; a person's passage is cut where someone else started speaking in the middle of it, so a "Yes" said while
/// another person talks lands where it was said.
/// </summary>
public static class PersonAudioMerger
{
    /// <summary>What one stretch of one person's own audio was heard to say. <paramref name="OffsetMs"/>: where it starts in the meeting.</summary>
    public sealed record Heard(int Participant, int OffsetMs, TranscriptionResult Result);

    /// <param name="names">Each participant's name (two people with the same name already told apart).</param>
    public static TranscriptionResult Combine(IReadOnlyList<Heard> parts, IReadOnlyDictionary<int, string> names, string engine)
    {
        var shifted = new List<(Segment Seg, int Who)>();
        foreach (var part in parts)
            foreach (var s in part.Result.Segments)
                shifted.Add((s with
                {
                    Id = Guid.NewGuid(), // each person's audio was transcribed on its own, so ids may repeat between them
                    StartMs = s.StartMs + part.OffsetMs, EndMs = s.EndMs + part.OffsetMs,
                    Words = s.Words?.Select(w => w with { StartMs = w.StartMs + part.OffsetMs, EndMs = w.EndMs + part.OffsetMs }).ToList(),
                }, part.Participant));

        // Cut a passage where another person started speaking inside it (only when its word timings match its text).
        var pieces = new List<(Segment Seg, int Who)>();
        foreach (var (seg, who) in shifted)
        {
            var cuts = shifted.Where(o => o.Who != who && o.Seg.StartMs > seg.StartMs && o.Seg.StartMs < seg.EndMs)
                .Select(o => o.Seg.StartMs).Distinct().Order().ToList();
            var tokens = cuts.Count == 0 ? null : SpeakerAligner.Tokens(seg);
            if (tokens is null) { pieces.Add((seg, who)); continue; }
            var run = new List<SpeakerAligner.Token>();
            var next = 0;
            void Flush()
            {
                if (run.Count == 0) return;
                pieces.Add((seg with
                {
                    Id = Guid.NewGuid(), StartMs = run[0].Start, EndMs = run[^1].End, Text = string.Join(" ", run.Select(t => t.Text)),
                    Words = [.. run.Select(t => new WordTiming(t.Text, t.Start, t.End))],
                }, who));
                run = [];
            }
            foreach (var t in tokens)
            {
                // A word that starts at or after the other person's start begins a new piece.
                if (next < cuts.Count && t.Start >= cuts[next])
                {
                    Flush();
                    while (next < cuts.Count && t.Start >= cuts[next]) next++;
                }
                run.Add(t);
            }
            Flush();
        }

        // Speakers in the order they first speak, named by the meeting platform.
        var speakers = new Dictionary<int, Speaker>();
        var segments = new List<Segment>();
        foreach (var (seg, who) in pieces.OrderBy(p => p.Seg.StartMs).ThenBy(p => p.Seg.EndMs))
        {
            if (!speakers.TryGetValue(who, out var sp)) speakers[who] = sp = new Speaker(Guid.NewGuid(), names.GetValueOrDefault(who) ?? $"Participant {who}");
            segments.Add(seg with { SpeakerId = sp.Id });
        }
        var warning = parts.Select(p => p.Result.Warning).FirstOrDefault(w => w is not null);
        return new TranscriptionResult(engine, speakers.Values.ToList(), AzureSpeechService.MergeTurns(segments), warning);
    }

    /// <summary>Each participant's name; two people with the same display name keep apart, the second gets a number.</summary>
    public static Dictionary<int, string> Names(IEnumerable<(int Participant, string Name)> people)
    {
        var names = new Dictionary<int, string>();
        foreach (var g in people.GroupBy(x => x.Participant)) names[g.Key] = g.First().Name;
        foreach (var dup in names.GroupBy(n => n.Value).Where(g => g.Count() > 1).ToList())
        {
            var k = 1;
            foreach (var n in dup.Skip(1)) names[n.Key] = $"{n.Value} ({++k})";
        }
        return names;
    }
}
