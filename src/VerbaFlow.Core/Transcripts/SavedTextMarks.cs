using VerbaFlow.Core.Domain;

namespace VerbaFlow.Core.Transcripts;

/// <summary>Where saved text sits in a passage: the code and the words it covers (word positions, counted on spaces).</summary>
public sealed record SavedTextMark(string Code, int From, int To);

/// <summary>
/// Finds the words a spoken "insert" put into a passage, so the page can mark them. The words are looked for in the
/// passage as it is now (capitals and punctuation around a word do not matter), so a mark stays after small edits around
/// the saved text and goes away when the saved text itself was changed.
/// </summary>
public static class SavedTextMarks
{
    public static IReadOnlyList<SavedTextMark> Find(Segment s, IReadOnlyList<SavedText>? saved = null)
    {
        if (s.Inserted is null || string.IsNullOrWhiteSpace(s.Text)) return [];
        var words = s.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(Bare).ToArray();
        var marks = new List<SavedTextMark>();
        var after = 0;
        foreach (var (code, text) in Looked(s, saved))
        {
            var wanted = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(Bare).Where(w => w.Length > 0).ToArray();
            if (wanted.Length == 0) continue;
            var at = IndexOf(words, wanted, after);
            if (at < 0) at = IndexOf(words, wanted, 0);
            if (at < 0 || marks.Any(m => at < m.To && at + wanted.Length > m.From)) continue;
            marks.Add(new SavedTextMark(code, at, at + wanted.Length));
            after = at + wanted.Length;
        }
        return marks.OrderBy(m => m.From).ToList();
    }

    /// <summary>The words to look for: as inserted, or for passages made before that was kept, the saved text's wording now.</summary>
    private static IEnumerable<(string Code, string Text)> Looked(Segment s, IReadOnlyList<SavedText>? saved)
    {
        if (s.InsertedParts is { Count: > 0 } parts) return parts.Select(p => (p.Code, p.Text));
        if (saved is null) return [];
        var codes = s.Inserted!.Split(", ", StringSplitOptions.RemoveEmptyEntries);
        return codes.SelectMany(code => saved.Where(t => SpokenCodes.Key(t.Code) == SpokenCodes.Key(code))
            .Take(1)
            .SelectMany(t => t.Text.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => (code, line))));
    }

    private static int IndexOf(string[] words, string[] wanted, int from)
    {
        for (var i = Math.Max(0, from); i + wanted.Length <= words.Length; i++)
        {
            var k = 0;
            while (k < wanted.Length && words[i + k] == wanted[k]) k++;
            if (k == wanted.Length) return i;
        }
        return -1;
    }

    private static string Bare(string w) => new string(w.Trim().ToLowerInvariant().SkipWhile(c => !char.IsLetterOrDigit(c)).Reverse()
        .SkipWhile(c => !char.IsLetterOrDigit(c)).Reverse().ToArray());
}
