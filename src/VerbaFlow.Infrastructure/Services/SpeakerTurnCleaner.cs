using VerbaFlow.Core.Providers;

namespace VerbaFlow.Infrastructure.Services;

/// <summary>
/// The local model tends to turn coughs, laughs and moments where one person's voice changes into extra "people" that spoke for
/// a second or two. This folds every voice group that spoke for less than a minimum total time into the real speaker nearest to it in time.
/// </summary>
public static class SpeakerTurnCleaner
{
    /// <returns>The cleaned turns, and how many small voice groups were folded away.</returns>
    public static (IReadOnlyList<SpeakerTurn> Turns, int Merged) AbsorbSmallVoices(IReadOnlyList<SpeakerTurn> turns, int minMs)
    {
        if (minMs <= 0 || turns.Count == 0) return (turns, 0);
        var total = turns.GroupBy(t => t.Speaker).ToDictionary(g => g.Key, g => g.Sum(t => t.EndMs - t.StartMs));
        var keep = total.Where(kv => kv.Value >= minMs).Select(kv => kv.Key).ToHashSet();
        if (keep.Count == 0) keep.Add(total.OrderByDescending(kv => kv.Value).First().Key); // everyone is small: keep the one who spoke most
        if (keep.Count == total.Count) return (turns, 0);

        var anchors = turns.Where(t => keep.Contains(t.Speaker)).ToList();
        var result = turns.Select(t =>
        {
            if (keep.Contains(t.Speaker)) return t;
            var nearest = anchors.OrderBy(a => Math.Max(0, Math.Max(a.StartMs - t.EndMs, t.StartMs - a.EndMs))).ThenBy(a => a.StartMs).First();
            return t with { Speaker = nearest.Speaker };
        }).ToList();
        return (result, total.Count - keep.Count);
    }
}
