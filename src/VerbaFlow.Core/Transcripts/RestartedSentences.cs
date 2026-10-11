namespace VerbaFlow.Core.Transcripts;

/// <summary>
/// Finds a sentence the speaker abandoned and started again: a short sentence (3 to 8 words) straight after which a
/// longer sentence begins with the same three words ("I will quite get it. I will quite clearly put out..."). Real text
/// can look the same ("We will meet on Monday. We will meet on Tuesday too."), so the caller shows a removal as a
/// tracked change rather than dropping it unseen.
/// </summary>
public static class RestartedSentences
{
    private const int MinWords = 3, MaxWords = 8, SameStart = 3;
    private const string Openers = "\"'(“‘[";

    /// <summary>The passage with restarted sentences taken out, or null when there are none. Kept words keep their timings.</summary>
    public static (Segment Segment, int Removed)? Remove(Segment s)
    {
        if (s.Inserted is not null) return null; // saved text is the company's own wording: never cross any of it out
        var tokens = s.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (tokens.Any(t => t.Contains('\n'))) return null; // typed paragraph breaks: leave alone
        var timed = s.Words is { } w && w.Count == tokens.Count ? w.ToList() : null;
        var removed = 0;
        var sentences = Sentences(tokens);
        // Walk backwards so earlier positions stay valid as words are removed.
        for (var k = sentences.Count - 2; k >= 0; k--)
        {
            var (a0, a1) = sentences[k];
            var (b0, b1) = sentences[k + 1];
            var len = a1 - a0;
            if (len < MinWords || len > MaxWords || b1 - b0 <= len) continue;
            if (!Enumerable.Range(0, SameStart).All(i => Key(tokens[a0 + i]) is { Length: > 0 } x && x == Key(tokens[b0 + i]))) continue;
            // A quote or bracket opened by the abandoned sentence still opens the restart.
            var opener = new string(tokens[a0].TakeWhile(c => Openers.Contains(c)).ToArray());
            if (opener.Length > 0 && !tokens[b0].StartsWith(opener)) tokens[b0] = opener + tokens[b0];
            tokens.RemoveRange(a0, len);
            timed?.RemoveRange(a0, len);
            removed++;
        }
        if (removed == 0) return null;
        var text = string.Join(' ', tokens);
        var words = timed?.Select((x, i) => x with { Text = tokens[i] }).ToList();
        return (s with { Id = Guid.NewGuid(), Text = text, Words = words, StartMs = words?.FirstOrDefault()?.StartMs ?? s.StartMs }, removed);
    }

    /// <summary>[start, end) word ranges of the sentences, each ending at a word that ends with . ? or ! (a closing quote or bracket may follow).</summary>
    private static List<(int Start, int End)> Sentences(List<string> tokens)
    {
        var result = new List<(int, int)>();
        var start = 0;
        for (var i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i].TrimEnd('"', '\'', ')', '”', '’', ']');
            if (t.EndsWith('.') || t.EndsWith('?') || t.EndsWith('!') || i == tokens.Count - 1) { result.Add((start, i + 1)); start = i + 1; }
        }
        return result;
    }

    private static string Key(string word) => new(word.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}
