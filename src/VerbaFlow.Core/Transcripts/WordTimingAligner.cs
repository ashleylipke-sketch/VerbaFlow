namespace VerbaFlow.Core.Transcripts;

/// <summary>
/// Gives every shown word a time. Speech services time the words as spoken ("two thousand nine", "comma"), which can
/// differ from the words shown ("2009", ","). Words that match keep the spoken word's time; the rest are spread evenly
/// across the gap between their matched neighbours, so following along stays in step instead of drifting.
/// </summary>
public static class WordTimingAligner
{
    public static List<WordTiming> Align(string shown, IReadOnlyList<WordTiming> spoken, int startMs, int endMs)
    {
        var tokens = shown.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var a = tokens.Select(Key).ToArray();
        var b = spoken.Select(w => Key(w.Text)).ToArray();

        // Longest common subsequence of the two word lists.
        var lcs = new int[a.Length + 1, b.Length + 1];
        for (var i = a.Length - 1; i >= 0; i--)
            for (var j = b.Length - 1; j >= 0; j--)
                lcs[i, j] = a[i].Length > 0 && a[i] == b[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
        var match = new int[a.Length];
        Array.Fill(match, -1);
        for (int i = 0, j = 0; i < a.Length && j < b.Length;)
        {
            // Prefer the later of two equal spoken words: a stumble ("I will quite... I will quite clearly") comes first.
            if (a[i].Length > 0 && a[i] == b[j] && lcs[i, j + 1] < lcs[i, j]) { match[i] = j; i++; j++; }
            else if (lcs[i, j + 1] == lcs[i, j]) j++;
            else i++;
        }

        var result = new WordTiming[tokens.Length];
        for (var i = 0; i < tokens.Length;)
        {
            if (match[i] >= 0) { var w = spoken[match[i]]; result[i] = new WordTiming(tokens[i], w.StartMs, w.EndMs); i++; continue; }
            // A run of unmatched words between two matched ones (or the passage edges).
            var k = i;
            while (k < tokens.Length && match[k] < 0) k++;
            var from = i == 0 ? startMs : result[i - 1].EndMs;
            var to = k < tokens.Length ? spoken[match[k]].StartMs : endMs;
            if (to < from) to = from;
            var n = k - i;
            for (var x = 0; x < n; x++)
                result[i + x] = new WordTiming(tokens[i + x], from + (int)((long)(to - from) * x / n), from + (int)((long)(to - from) * (x + 1) / n));
            i = k;
        }
        return [.. result];
    }

    private static string Key(string word) => new(word.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}
