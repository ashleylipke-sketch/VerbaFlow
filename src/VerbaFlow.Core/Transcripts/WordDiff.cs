namespace VerbaFlow.Core.Transcripts;

/// <summary>
/// Word-by-word comparison of a passage and a suggested correction. Words are separated by spaces only, so a word keeps
/// its punctuation ("Smith," differs from "Smith") and a typed line break stays inside its word.
/// </summary>
public static class WordDiff
{
    /// <summary>A run of equal words (Removed and Added the same), or one change: words taken out and words put in.</summary>
    public sealed record Part(IReadOnlyList<string> Removed, IReadOnlyList<string> Added, bool Same);

    public static string[] Words(string text) => text.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    public static IReadOnlyList<Part> Compare(string before, string after)
    {
        var a = Words(before);
        var b = Words(after);
        // Longest common subsequence, then walked from the start.
        var l = new int[a.Length + 1, b.Length + 1];
        for (var i = a.Length - 1; i >= 0; i--)
            for (var j = b.Length - 1; j >= 0; j--)
                l[i, j] = a[i] == b[j] ? l[i + 1, j + 1] + 1 : Math.Max(l[i + 1, j], l[i, j + 1]);
        var parts = new List<Part>();
        List<string> same = [], removed = [], added = [];
        void FlushChange() { if (removed.Count + added.Count > 0) { parts.Add(new Part(removed, added, false)); removed = []; added = []; } }
        void FlushSame() { if (same.Count > 0) { parts.Add(new Part(same, same, true)); same = []; } }
        int x = 0, y = 0;
        while (x < a.Length || y < b.Length)
        {
            if (x < a.Length && y < b.Length && a[x] == b[y]) { FlushChange(); same.Add(a[x]); x++; y++; }
            else if (y < b.Length && (x == a.Length || l[x, y + 1] >= l[x + 1, y])) { FlushSame(); added.Add(b[y++]); }
            else { FlushSame(); removed.Add(a[x++]); }
        }
        FlushChange(); FlushSame();
        return parts;
    }

    /// <summary>
    /// How much of the wording a suggestion keeps, ignoring capitals and punctuation (1 = the same words). A grammar
    /// check should keep nearly all of it; a low share means the passage was rewritten, not corrected.
    /// </summary>
    public static double WordingKept(string before, string after)
    {
        static string Bare(string w) => new string(w.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        var a = Words(before).Select(Bare).Where(w => w.Length > 0).ToArray();
        var b = Words(after).Select(Bare).Where(w => w.Length > 0).ToArray();
        if (a.Length == 0 && b.Length == 0) return 1;
        var kept = Compare(string.Join(' ', a), string.Join(' ', b)).Where(p => p.Same).Sum(p => p.Removed.Count);
        return (double)kept / Math.Max(a.Length, b.Length);
    }
}
