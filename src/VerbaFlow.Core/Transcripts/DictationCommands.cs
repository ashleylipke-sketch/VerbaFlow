using System.Globalization;

namespace VerbaFlow.Core.Transcripts;

/// <summary>
/// Spoken commands in a dictation ("comma", "full stop", "new paragraph") turned into punctuation and paragraphs.
/// Works on the words the speech service returned, so each kept word keeps its timing. The speech service adds its own
/// punctuation too, so a command replaces any punctuation it put next to the command word.
/// "New paragraph" and "new line" both start a new passage. "Insert" followed by a saved text's code ("insert legal 1.1")
/// puts that text in its place; its words are timed across the spoken command, and its own line breaks start paragraphs.
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

    private sealed class Token(string text, WordTiming? timing, int index, string? insert = null)
    {
        public string Text = text;
        public readonly WordTiming? Timing = timing;
        public readonly int Index = index; // position among the words the speech service returned
        public readonly string? Insert = insert; // the saved text's code, for words put in by "insert"
    }

    /// <summary>A saved text ready to insert: its code as typed, and its words paragraph by paragraph.</summary>
    private sealed record Insert(string Code, List<string[]> Paragraphs);

    private sealed class Inserts
    {
        public readonly Dictionary<string, Insert> ByKey = new();
        public readonly HashSet<string> FirstWords = [];
    }

    /// <summary>The longest run of words after "insert" that is a saved text's code.</summary>
    private const int MaxCodeWords = 12;
    /// <summary>Said before a code, in English and Afrikaans ("voeg in" is two words and splits around the code, so "invoeg" only).</summary>
    private static readonly HashSet<string> InsertWords = ["insert", "invoeg"];
    public const string NoMatch = "insert, no saved text matched";

    private static Inserts? Prepare(IReadOnlyList<(string Code, string Text)>? saved)
    {
        if (saved is null || saved.Count == 0) return null;
        var result = new Inserts();
        foreach (var (code, text) in saved)
        {
            var key = SpokenCodes.Key(code);
            if (key.Length == 0) continue;
            var paragraphs = text.Replace("\r\n", "\n").Split(LineBreakChars)
                .Select(l => l.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Where(w => w.Length > 0).ToList();
            if (paragraphs.Count == 0) continue;
            result.ByKey.TryAdd(key, new Insert(code, paragraphs));
            result.FirstWords.Add(key.Split(' ')[0]);
        }
        return result.ByKey.Count == 0 ? null : result;
    }

    private static (Insert Insert, int Length)? FindInsert(string[] raw, int from, Inserts inserts)
    {
        var limit = 0;
        while (limit < MaxCodeWords && from + limit < raw.Length && raw[from + limit] != LineBreak) limit++;
        for (var k = limit; k >= 1; k--)
        {
            // A trailing "comma" or "full stop" belongs to the dictation, not to the code.
            if (!SpokenCodes.HasContent(raw[from + k - 1])) continue;
            if (inserts.ByKey.TryGetValue(SpokenCodes.Key(raw[from..(from + k)]), out var hit)) return (hit, k);
        }
        return null;
    }

    /// <summary>Applies the commands to every passage. A passage is split where a paragraph was asked for.</summary>
    public static (IReadOnlyList<Segment> Segments, int Applied) Apply(IReadOnlyList<Segment> segments)
    {
        var (result, applied, _) = ApplyCounted(segments);
        return (result, applied);
    }

    /// <summary>As <see cref="Apply(IReadOnlyList{Segment})"/>, also saying how often each command was heard (command names only, never the dictated words).</summary>
    public static (IReadOnlyList<Segment> Segments, int Applied, IReadOnlyDictionary<string, int> Heard) ApplyCounted(IReadOnlyList<Segment> segments,
        IReadOnlyList<(string Code, string Text)>? savedText = null)
    {
        var inserts = Prepare(savedText);
        var heard = new SortedDictionary<string, int>();
        var result = new List<Segment>();
        var applied = 0;
        // The speech service splits speech into passages at pauses; in a dictation only "new paragraph" starts a new paragraph.
        var startNext = true;
        foreach (var s in segments)
        {
            var (parts, n, breakAtStart, breakAtEnd) = Run(s, heard, inserts);
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
        var (parts, n, _, _) = Run(s, null, null);
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

    private static (List<Segment> Segments, int Applied, bool BreakAtStart, bool BreakAtEnd) Run(Segment s, IDictionary<string, int>? heard, Inserts? inserts)
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
            if (inserts is not null && raw[i] != LineBreak && InsertWords.Contains(Bare(raw[i])))
            {
                if (FindInsert(raw, i + 1, inserts) is { } hit)
                {
                    applied++;
                    Count(heard, "insert " + hit.Insert.Code);
                    // The inserted words share out the time it took to say "insert legal 1.1", so highlighting passes over them then.
                    var from = timed?[wordAt[i]].StartMs ?? 0;
                    var to = timed?[wordAt[i + hit.Length]].EndMs ?? 0;
                    var total = hit.Insert.Paragraphs.Sum(p => p.Length);
                    var n = 0;
                    for (var p = 0; p < hit.Insert.Paragraphs.Count; p++)
                    {
                        if (p > 0 && paragraphs[^1].Count > 0) paragraphs.Add([]);
                        foreach (var word in hit.Insert.Paragraphs[p])
                        {
                            var text = word;
                            if (openNext is not null) { text = openNext + text; openNext = null; }
                            if (capitalNext) { text = Capitalise(text); capitalNext = false; }
                            WordTiming? timing = timed is null ? null
                                : new(text, from + (int)((long)(to - from) * n / total), from + (int)((long)(to - from) * (n + 1) / total));
                            paragraphs[^1].Add(new Token(text, timing, i, hit.Insert.Code));
                            n++;
                        }
                    }
                    i += 1 + hit.Length;
                    continue;
                }
                if (i + 1 < raw.Length && inserts.FirstWords.Contains(SpokenCodes.Key([raw[i + 1]]).Split(' ')[0])) Count(heard, NoMatch);
            }
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
            Count(heard, NameOf(cmd));
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
            var codes = tokens.Select(t => t.Insert).OfType<string>().Distinct().ToList();
            segments.Add(s with
            {
                Id = Guid.NewGuid(),
                Text = string.Join(' ', tokens.Select(t => t.Text)), StartMs = start, EndMs = Math.Max(start, end), Words = words,
                Inserted = codes.Count == 0 ? null : string.Join(", ", codes)
            });
        }
        return (segments, applied, breakAtStart, breakAtEnd);
    }

    private static void Count(IDictionary<string, int>? heard, string name)
    {
        if (heard is not null) heard[name] = heard.TryGetValue(name, out var c) ? c + 1 : 1;
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
