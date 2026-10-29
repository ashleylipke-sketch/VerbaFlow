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
        var match = Match(tokens, spoken);

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

    /// <summary>Spoken punctuation and layout commands, as the speech service lists the spoken words.</summary>
    private static readonly string[][] Commands =
    [
        ["full", "stop"], ["comma"], ["question", "mark"], ["exclamation", "mark"], ["exclamation", "point"],
        ["colon"], ["semicolon"], ["semi", "colon"], ["new", "paragraph"], ["new", "line"],
    ];

    /// <summary>
    /// The speech service sometimes writes a spoken command as its mark ("full stop" as "."), and the mark then cannot be
    /// told apart from punctuation it added by itself, which is taken away when only spoken punctuation is kept. Its list
    /// of spoken words still has "full stop", so a command heard there but missing from the shown words is put back as
    /// words at that place, where the dictation commands then turn it into the mark.
    /// </summary>
    public static string RestoreSpokenCommands(string shown, IReadOnlyList<WordTiming> spoken)
    {
        var tokens = shown.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0 || spoken.Count == 0) return shown;
        var match = Match(tokens, spoken);
        var matchedSpoken = new bool[spoken.Count];
        foreach (var j in match) if (j >= 0) matchedSpoken[j] = true;
        var b = spoken.Select(w => Key(w.Text)).ToArray();
        var inserts = new SortedDictionary<int, List<string>>();
        for (var j = 0; j < b.Length; j++)
        {
            if (matchedSpoken[j]) continue;
            var cmd = Commands.FirstOrDefault(c => j + c.Length <= b.Length
                && c.Select((word, x) => !matchedSpoken[j + x] && b[j + x] == word).All(ok => ok));
            if (cmd is null) continue;
            var at = LastShownBefore(match, j);
            if (!inserts.TryGetValue(at, out var list)) inserts[at] = list = [];
            list.Add(string.Join(' ', cmd));
            j += cmd.Length - 1;
        }
        if (inserts.Count == 0) return shown;
        // Put the words in after the shown word they follow, keeping the rest of the text (line breaks too) as it was.
        var spans = System.Text.RegularExpressions.Regex.Matches(shown, @"\S+");
        var sb = new System.Text.StringBuilder(shown);
        foreach (var (at, words) in inserts.Reverse())
        {
            var phrase = string.Join(' ', words);
            if (at < 0) sb.Insert(spans[0].Index, phrase + " ");
            else sb.Insert(spans[at].Index + spans[at].Length, " " + phrase);
        }
        return sb.ToString();
    }

    /// <summary>The last shown word matched to a spoken word before spoken word j (-1 when there is none).</summary>
    private static int LastShownBefore(int[] match, int j)
    {
        var best = -1;
        for (var i = 0; i < match.Length; i++) if (match[i] >= 0 && match[i] < j) best = i;
        return best;
    }

    private static int[] Match(string[] tokens, IReadOnlyList<WordTiming> spoken)
    {
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

        return match;
    }

    private static string Key(string word) => new(word.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}
