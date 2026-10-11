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
        var (result, applied, _) = ApplyCounted(segments);
        return (result, applied);
    }

    /// <summary>As <see cref="Apply(IReadOnlyList{Segment})"/>, also saying how often each command was heard (command names only, never the dictated words).</summary>
    public static (IReadOnlyList<Segment> Segments, int Applied, IReadOnlyDictionary<string, int> Heard) ApplyCounted(IReadOnlyList<Segment> segments)
    {
        var heard = new SortedDictionary<string, int>();
        var result = new List<Segment>();
        var applied = 0;
        // The speech service splits speech into passages at pauses; in a dictation only "new paragraph" starts a new paragraph.
        var startNext = true;
        foreach (var s in segments)
        {
            var (parts, n, breakAtStart, breakAtEnd) = Run(s, heard);
            if (parts.Count > 0)
            {
                result.Add(parts[0] with { NewParagraph = startNext || breakAtStart });
                result.AddRange(parts.Skip(1).Select(x => x with { NewParagraph = true }));
                startNext = breakAtEnd;
            }
            else startNext = startNext || breakAtStart || breakAtEnd;
            applied += n;
        }
        return (result, applied, heard);
    }

    public static (IReadOnlyList<Segment> Segments, int Applied) Apply(Segment s)
    {
        var (parts, n, _, _) = Run(s, null);
        return (parts, n);
    }

    /// <summary>Passages grouped into paragraphs, in order.</summary>
    public static List<List<T>> Paragraphs<T>(IEnumerable<T> items, Func<T, Segment> segment)
    {
        var result = new List<List<T>>();
        foreach (var x in items)
        {
            if (result.Count == 0 || segment(x).NewParagraph != false) result.Add([]);
            result[^1].Add(x);
        }
        return result;
    }

    /// <summary>Stands for a line break the speech service itself put in its text (it may turn "new paragraph" into one).</summary>
    private const string LineBreak = "\uE000";
    private static readonly Command SpeechServiceBreak = new([LineBreak], Kind.Break);
    /// <summary>Every kind of line or paragraph break a service might write: \n, \r, Unicode line and paragraph separators, vertical tab, form feed, next line.</summary>
    public static readonly char[] LineBreakChars = ['\n', '\r', '\u2028', '\u2029', '\v', '\f', '\u0085'];

    private static string MarkLineBreaks(string text)
    {
        if (text.IndexOfAny(LineBreakChars) < 0) return text;
        var sb = new System.Text.StringBuilder(text.Length + 8);
        for (var i = 0; i < text.Length; i++)
        {
            if (Array.IndexOf(LineBreakChars, text[i]) < 0) { sb.Append(text[i]); continue; }
            if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
            sb.Append(' ').Append(LineBreak).Append(' ');
        }
        return sb.ToString();
    }

    private static string NameOf(Command c) => c == SpeechServiceBreak ? "line break from the speech service" : string.Join(' ', c.Words);

    private static (List<Segment> Segments, int Applied, bool BreakAtStart, bool BreakAtEnd) Run(Segment s, IDictionary<string, int>? heard)
    {
        var raw = MarkLineBreaks(s.Text).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        // Word timings line up with the spoken words, not with line breaks.
        var wordAt = new int[raw.Length];
        for (int i = 0, n = 0; i < raw.Length; i++) wordAt[i] = raw[i] == LineBreak ? -1 : n++;
        var timed = s.Words is { } w && w.Count == raw.Count(x => x != LineBreak) ? w : null;
        var paragraphs = new List<List<Token>> { new() };
        var applied = 0;
        var capitalNext = false;
        var breakAtStart = false;
        string? openNext = null;
        for (var i = 0; i < raw.Length;)
        {
            var cmd = raw[i] == LineBreak ? SpeechServiceBreak : Commands.FirstOrDefault(c => Matches(raw, i, c.Words));
            var current = paragraphs[^1];
            var prev = current.Count > 0 ? current[^1] : null;
            if (cmd is null)
            {
                var text = raw[i];
                if (openNext is not null) { text = openNext + text; openNext = null; }
                if (capitalNext) { text = Capitalise(text); capitalNext = false; }
                current.Add(new Token(text, timed?[wordAt[i]], i));
                i++;
                continue;
            }
            applied++;
            i += cmd.Words.Length;
            if (heard is not null) heard[NameOf(cmd)] = heard.TryGetValue(NameOf(cmd), out var c) ? c + 1 : 1;
            switch (cmd.Kind)
            {
                case Kind.Break:
                    if (current.Count > 0) paragraphs.Add([]);
                    else if (paragraphs.Count == 1) breakAtStart = true;
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
                    // Punctuation before a closing quote or bracket stays inside it: "I'm going to," not "I'm going to".
                    if (prev is not null) prev.Text += cmd.Symbol;
                    break;
            }
        }
        if (applied == 0) return ([s], 0, false, false);

        var breakAtEnd = paragraphs.Count > 1 && paragraphs[^1].Count == 0;
        var kept = paragraphs.Where(p => p.Count > 0).ToList();
        if (kept.Count == 0) return ([], applied, breakAtStart, false); // the passage was nothing but commands
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
        return (segments, applied, breakAtStart, breakAtEnd);
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
