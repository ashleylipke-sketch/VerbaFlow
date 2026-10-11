using System.Globalization;

namespace VerbaFlow.Core.Transcripts;

/// <summary>
/// Spoken commands in a dictation ("comma", "full stop", "new paragraph") turned into punctuation and paragraphs.
/// Works on the words the speech service returned, so each kept word keeps its timing. The speech service adds its own
/// punctuation too, so a command replaces any punctuation it put next to the command word.
/// "New paragraph" and "new line" both start a new passage.
/// </summary>
public static class DictationCommands
{
    private enum Kind { Break, Punctuation, Open, Close }

    private sealed record Command(string[] Words, Kind Kind, string Symbol = "");

    /// <summary>Longest first, so "question mark" wins over a shorter command starting the same way.</summary>
    private static readonly Command[] Commands = new Command[]
    {
        new(["new", "paragraph"], Kind.Break), new(["next", "paragraph"], Kind.Break), new(["new", "line"], Kind.Break), new(["next", "line"], Kind.Break),
        new(["full", "stop"], Kind.Punctuation, "."), new(["question", "mark"], Kind.Punctuation, "?"),
        new(["exclamation", "mark"], Kind.Punctuation, "!"), new(["comma"], Kind.Punctuation, ","),
        new(["semicolon"], Kind.Punctuation, ";"), new(["semi", "colon"], Kind.Punctuation, ";"), new(["colon"], Kind.Punctuation, ":"),
        new(["open", "bracket"], Kind.Open, "("), new(["close", "bracket"], Kind.Close, ")"),
        new(["open", "quote"], Kind.Open, "\""), new(["close", "quote"], Kind.Close, "\""),
        new(["open", "quotes"], Kind.Open, "\""), new(["close", "quotes"], Kind.Close, "\""),
        // Afrikaans
        new(["nuwe", "paragraaf"], Kind.Break), new(["nuwe", "reël"], Kind.Break), new(["nuwe", "reel"], Kind.Break),
        new(["komma"], Kind.Punctuation, ","), new(["vraagteken"], Kind.Punctuation, "?"), new(["uitroepteken"], Kind.Punctuation, "!"),
        new(["dubbelpunt"], Kind.Punctuation, ":"), new(["kommapunt"], Kind.Punctuation, ";"),
    }.OrderByDescending(c => c.Words.Length).ToArray();

    /// <summary>The commands a person can say, for showing on screen.</summary>
    public static readonly IReadOnlyList<(string Say, string Gives)> Help =
    [
        ("new paragraph / new line", "starts a new paragraph"), ("full stop", "."), ("comma", ","), ("question mark", "?"),
        ("exclamation mark", "!"), ("colon", ":"), ("semicolon", ";"), ("open bracket / close bracket", "( )"),
        ("open quote / close quote", "\" \""),
    ];

    private const string Trailing = ".,;:!?";

    private sealed class Token(string text, WordTiming? timing, int index)
    {
        public string Text = text;
        public readonly WordTiming? Timing = timing;
        public readonly int Index = index; // position among the words the speech service returned
    }

    /// <summary>Applies the commands to every passage. A passage is split where a paragraph was asked for.</summary>
    public static (IReadOnlyList<Segment> Segments, int Applied) Apply(IReadOnlyList<Segment> segments)
    {
        var result = new List<Segment>();
        var applied = 0;
        foreach (var s in segments)
        {
            var (parts, n) = Apply(s);
            result.AddRange(parts);
            applied += n;
        }
        return (result, applied);
    }

    public static (IReadOnlyList<Segment> Segments, int Applied) Apply(Segment s)
    {
        var raw = s.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var timed = s.Words is { } w && w.Count == raw.Length ? w : null;
        var paragraphs = new List<List<Token>> { new() };
        var applied = 0;
        var capitalNext = false;
        string? openNext = null;
        for (var i = 0; i < raw.Length;)
        {
            var cmd = Commands.FirstOrDefault(c => Matches(raw, i, c.Words));
            var current = paragraphs[^1];
            var prev = current.Count > 0 ? current[^1] : null;
            if (cmd is null)
            {
                var text = raw[i];
                if (openNext is not null) { text = openNext + text; openNext = null; }
                if (capitalNext) { text = Capitalise(text); capitalNext = false; }
                current.Add(new Token(text, timed?[i], i));
                i++;
                continue;
            }
            applied++;
            i += cmd.Words.Length;
            switch (cmd.Kind)
            {
                case Kind.Break:
                    if (current.Count > 0) paragraphs.Add([]);
                    capitalNext = true;
                    break;
                case Kind.Punctuation:
                    if (prev is not null) prev.Text = prev.Text.TrimEnd(Trailing.ToCharArray()) + cmd.Symbol;
                    capitalNext = cmd.Symbol is "." or "?" or "!";
                    break;
                case Kind.Open:
                    openNext = cmd.Symbol;
                    break;
                case Kind.Close:
                    if (prev is not null) prev.Text = prev.Text.TrimEnd(Trailing.ToCharArray()) + cmd.Symbol;
                    break;
            }
        }
        if (applied == 0) return ([s], 0);

        var kept = paragraphs.Where(p => p.Count > 0).ToList();
        if (kept.Count == 0) return ([], applied); // the passage was nothing but commands
        var segments = new List<Segment>();
        for (var p = 0; p < kept.Count; p++)
        {
            var tokens = kept[p];
            var start = p == 0 ? s.StartMs : tokens[0].Timing?.StartMs ?? Proportional(s, raw.Length, tokens[0].Index);
            var end = p == kept.Count - 1 ? s.EndMs : kept[p + 1][0].Timing?.StartMs ?? Proportional(s, raw.Length, kept[p + 1][0].Index);
            var words = timed is null ? null : tokens.Select(t => t.Timing! with { Text = t.Text }).ToList();
            segments.Add(s with
            {
                Id = Guid.NewGuid(),
                Text = string.Join(' ', tokens.Select(t => t.Text)), StartMs = start, EndMs = Math.Max(start, end), Words = words
            });
        }
        return (segments, applied);
    }

    private static bool Matches(string[] raw, int at, string[] words)
    {
        if (at + words.Length > raw.Length) return false;
        for (var k = 0; k < words.Length; k++)
            if (!string.Equals(Bare(raw[at + k]), words[k], StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    /// <summary>The word without the punctuation the speech service put around it.</summary>
    private static string Bare(string token) => token.Trim().Trim(Trailing.ToCharArray()).Trim('"', '(', ')').ToLowerInvariant();

    private static string Capitalise(string text)
    {
        var i = 0;
        while (i < text.Length && !char.IsLetter(text[i])) i++;
        return i < text.Length ? text[..i] + char.ToUpper(text[i], CultureInfo.InvariantCulture) + text[(i + 1)..] : text;
    }

    /// <summary>Without word timings, a paragraph's start is estimated from where its first word sits in the passage.</summary>
    private static int Proportional(Segment s, int count, int index) =>
        s.StartMs + (int)((long)(s.EndMs - s.StartMs) * index / Math.Max(1, count));
}
