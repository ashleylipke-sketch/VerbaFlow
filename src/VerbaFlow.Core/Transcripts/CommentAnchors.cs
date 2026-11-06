namespace VerbaFlow.Core.Transcripts;

/// <summary>Where a comment's words are in the text now: the passage and word positions [From, To).</summary>
public sealed record CommentPlace(Guid SegmentId, int From, int To);

/// <summary>
/// Finds the words a comment was made on in the text as it is now. An edit gives a passage a new id but keeps its start
/// time, so the passage with the same id is tried first, then passages starting at the same moment, then those around it,
/// then the rest. Capitals and punctuation around a word do not matter; of several matches the one nearest the comment's
/// original word position wins. When the words themselves were changed, nothing is found and the comment stays listed.
/// </summary>
public static class CommentAnchors
{
    public static string[] Words(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    public static CommentPlace? Locate(IReadOnlyList<Segment> current, Guid segmentId, int segmentStartMs, int from, string quote)
    {
        var wanted = Words(quote).Select(Bare).ToArray();
        if (wanted.All(w => w.Length == 0)) return null;
        var order = current
            .Select((s, i) => (s, i))
            .OrderBy(x => x.s.Id == segmentId ? 0 : x.s.StartMs == segmentStartMs ? 1 : x.s.StartMs <= segmentStartMs && x.s.EndMs > segmentStartMs ? 2 : 3)
            .ThenBy(x => Math.Abs(x.s.StartMs - segmentStartMs))
            .Select(x => x.s);
        foreach (var s in order)
        {
            var words = Words(s.Text).Select(Bare).ToArray();
            var best = -1;
            for (var i = 0; i + wanted.Length <= words.Length; i++)
            {
                var k = 0;
                while (k < wanted.Length && words[i + k] == wanted[k]) k++;
                if (k == wanted.Length && (best < 0 || Math.Abs(i - from) < Math.Abs(best - from))) best = i;
            }
            if (best >= 0) return new CommentPlace(s.Id, best, best + wanted.Length);
        }
        return null;
    }

    private static string Bare(string w) => new string(w.Trim().ToLowerInvariant().SkipWhile(c => !char.IsLetterOrDigit(c)).Reverse()
        .SkipWhile(c => !char.IsLetterOrDigit(c)).Reverse().ToArray());
}
