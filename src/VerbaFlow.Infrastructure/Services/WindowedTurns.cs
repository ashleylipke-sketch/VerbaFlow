using VerbaFlow.Core.Providers;

namespace VerbaFlow.Infrastructure.Services;

/// <summary>Cuts speech into short overlapping windows and turns one group number per window back into speaker turns.</summary>
public static class WindowedTurns
{
    public const int WindowMs = 1500, HopMs = 750, ShortestMs = 600;

    public sealed record Window(int Region, int StartMs, int EndMs);

    /// <summary>Joins turns of any speakers into stretches where somebody is talking (gaps under <paramref name="gapMs"/> are bridged).</summary>
    public static List<(int StartMs, int EndMs)> SpeechRegions(IReadOnlyList<SpeakerTurn> turns, int gapMs = 300)
    {
        var regions = new List<(int, int)>();
        foreach (var t in turns.OrderBy(t => t.StartMs))
        {
            if (regions.Count > 0 && t.StartMs - regions[^1].Item2 <= gapMs) regions[^1] = (regions[^1].Item1, Math.Max(regions[^1].Item2, t.EndMs));
            else regions.Add((t.StartMs, t.EndMs));
        }
        return regions;
    }

    public static List<Window> MakeWindows(IReadOnlyList<(int StartMs, int EndMs)> regions)
    {
        var windows = new List<Window>();
        for (var r = 0; r < regions.Count; r++)
        {
            var (s, e) = regions[r];
            var len = e - s;
            if (len < ShortestMs) continue;
            if (len <= WindowMs) { windows.Add(new(r, s, e)); continue; }
            for (var w = s; ; w += HopMs)
            {
                if (w + WindowMs >= e) { windows.Add(new(r, e - WindowMs, e)); break; }
                windows.Add(new(r, w, w + WindowMs));
            }
        }
        return windows;
    }

    /// <summary>Each window owns the time up to the middle of the overlap with its neighbour; labels are smoothed so a single odd window cannot split a sentence.</summary>
    public static List<SpeakerTurn> ToTurns(IReadOnlyList<(int StartMs, int EndMs)> regions, IReadOnlyList<Window> windows, IReadOnlyList<int> labels)
    {
        var turns = new List<SpeakerTurn>();
        foreach (var group in windows.Select((w, i) => (w, i)).GroupBy(x => x.w.Region))
        {
            var items = group.ToList();
            var own = items.Select(x => labels[x.i]).ToList();
            var smooth = own.ToList();
            for (var i = 1; i < own.Count - 1; i++)
                if (own[i - 1] == own[i + 1] && own[i] != own[i - 1]) smooth[i] = own[i - 1];
            var (rs, re) = regions[group.Key];
            for (var i = 0; i < items.Count; i++)
            {
                var start = i == 0 ? rs : (items[i - 1].w.EndMs + items[i].w.StartMs) / 2;
                var end = i == items.Count - 1 ? re : (items[i].w.EndMs + items[i + 1].w.StartMs) / 2;
                if (end <= start) continue;
                if (turns.Count > 0 && turns[^1].Speaker == smooth[i] && turns[^1].EndMs >= start - 1)
                    turns[^1] = turns[^1] with { EndMs = end };
                else turns.Add(new SpeakerTurn(start, end, smooth[i]));
            }
        }
        return turns;
    }
}
