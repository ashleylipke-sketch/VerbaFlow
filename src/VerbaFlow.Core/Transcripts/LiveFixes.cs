namespace VerbaFlow.Core.Transcripts;

/// <summary>
/// A correction typed into Speech mode's live text while speaking: the passage that started at <paramref name="StartMs"/>
/// read <paramref name="Original"/> and the person changed it to <paramref name="Text"/>.
/// </summary>
public sealed record LiveFix(int StartMs, string Original, string Text);

/// <summary>
/// Puts typed corrections back into the live text each time it is worked out again from the phrases heard. A passage
/// may have grown since it was corrected (the person kept speaking): the corrected words then stand in for the start
/// of it and the newer words follow. A correction whose words are no longer there (for example after "scratch that")
/// is left out.
/// </summary>
public static class LiveFixes
{
    public const int MaxFixes = 200;
    public const int MaxLength = 5000;
    private const int Slack = 40; // ms: the same passage can start a little differently once more is heard
    private static readonly char[] Tail = ['.', ',', ';', ':', '!', '?', ' ', '\n'];

    /// <summary>The passages with the corrections applied, and which passages (by position) were corrected.</summary>
    public static (List<Segment> Segments, HashSet<int> Fixed) Apply(IEnumerable<Segment> segments, IReadOnlyList<LiveFix> fixes)
    {
        var list = segments.ToList();
        var done = new HashSet<int>();
        if (fixes.Count == 0) return (list, done);
        for (var i = 0; i < list.Count; i++)
        {
            var s = list[i];
            // Corrections are put in in the order they were made: a later one was made on the text as already corrected.
            var text = s.Text;
            foreach (var fix in fixes)
                if (Math.Abs(fix.StartMs - s.StartMs) <= Slack && Fit(text, fix) is { } fitted) { text = fitted; done.Add(i); }
            if (done.Contains(i)) list[i] = s with { Text = text, Words = null, LowConfidence = false };
        }
        return (list, done);
    }

    /// <summary>The passage's text with the correction put in, or null when the corrected words are no longer there.</summary>
    public static string? Fit(string current, LiveFix fix)
    {
        if (current == fix.Original) return fix.Text;
        if (current.StartsWith(fix.Original, StringComparison.Ordinal)) return Join(fix.Text, current[fix.Original.Length..]);
        // More was heard after it: punctuation at the end of the corrected passage may have changed ("word." became "word,").
        var core = fix.Original.TrimEnd(Tail);
        if (core.Length == 0 || !current.StartsWith(core, StringComparison.Ordinal)) return null;
        var tail = fix.Original[core.Length..];
        var text = tail.Length > 0 && fix.Text.EndsWith(tail, StringComparison.Ordinal) ? fix.Text[..^tail.Length] : fix.Text.TrimEnd(Tail);
        return Join(text, current[core.Length..]);
    }

    private static string Join(string a, string rest) =>
        rest.Length == 0 ? a : a.Length > 0 && !char.IsWhiteSpace(a[^1]) && !char.IsWhiteSpace(rest[0]) && char.IsLetterOrDigit(rest[0]) ? a + " " + rest : a + rest;
}
