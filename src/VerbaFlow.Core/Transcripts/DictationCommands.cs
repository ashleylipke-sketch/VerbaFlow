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
    private enum Kind { Break, Punctuation, Open, Close, ListPoint }

    private sealed record Command(string[] Words, Kind Kind, string Symbol = "");

    /// <summary>Longest first, so "question mark" wins over a shorter command starting the same way.</summary>
    private static readonly Command[] Commands = new Command[]
    {
        new(["bullet", "point"], Kind.ListPoint, Bullet), new(["numbered", "point"], Kind.ListPoint, Number), new(["number", "point"], Kind.ListPoint, Number),
        // The speech service sometimes hears the singular as a plural ("numbered points").
        new(["bullet", "points"], Kind.ListPoint, Bullet), new(["numbered", "points"], Kind.ListPoint, Number), new(["number", "points"], Kind.ListPoint, Number),
        // People also say "insert bullet" (owner, 2026-10-21). A saved text whose code starts with "bullet" still wins.
        new(["insert", "bullet", "point"], Kind.ListPoint, Bullet), new(["insert", "bullet"], Kind.ListPoint, Bullet),
        new(["insert", "numbered", "point"], Kind.ListPoint, Number), new(["insert", "number", "point"], Kind.ListPoint, Number),
        new(["end", "list"], Kind.Break), new(["end", "lists"], Kind.Break), new(["end", "of", "list"], Kind.Break),
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
        ("new paragraph / new line", "starts a new paragraph"), ("bullet point (or insert bullet)", "starts a bulleted point"),
        ("numbered point", "starts the next numbered point (1, 2, 3 counted for you)"), ("end list", "back to ordinary paragraphs"), ("full stop", "."), ("comma", ","), ("question mark", "?"),
        ("exclamation mark", "!"), ("colon", ":"), ("semicolon", ";"), ("open bracket / close bracket", "( )"),
        ("open quote / close quote", "\" \""),
        ("scratch that", "takes away what you said since the last punctuation mark"),
        ("replace Tuesday with Thursday", "changes the last \"Tuesday\" you said"),
    ];

    private const string Trailing = ".,;:!?";
    public const string Bullet = "bullet";
    public const string Number = "number";

    /// <summary>
    /// What goes in front of each paragraph: "•" for a bullet point, "1.", "2." for numbered points (counted afresh after
    /// anything that is not a numbered point), null for an ordinary paragraph.
    /// </summary>
    public static List<string?> ListMarkers(IEnumerable<string?> lists)
    {
        var result = new List<string?>();
        var n = 0;
        foreach (var l in lists)
        {
            n = l == Number ? n + 1 : 0;
            result.Add(l switch { Bullet => "•", Number => $"{n}.", _ => null });
        }
        return result;
    }

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
        IReadOnlyList<(string Code, string Text)>? savedText = null, IEnumerable<string>? names = null, bool spokenPunctuationOnly = false)
    {
        var flow = new Flow(spokenPunctuationOnly);
        var inserts = Prepare(savedText);
        var keep = KeepCapitals(segments, names);
        var heard = new SortedDictionary<string, int>();
        var result = new List<Segment>();
        var applied = 0;
        // The speech service splits speech into passages at pauses; in a dictation only "new paragraph" starts a new paragraph.
        var startNext = true;
        string? nextList = null; // the kind of list point the next paragraph starts, when "bullet point" ended a passage
        foreach (var s in segments)
        {
            if (startNext) flow.LowerNext = false;
            var (parts, n, breakAtStart, breakAtEnd, startList, endList) = Run(s, heard, inserts, keep, flow);
            if (parts.Count > 0)
            {
                var first = breakAtStart ? startList : startNext ? nextList : null;
                result.Add(parts[0] with { NewParagraph = startNext || breakAtStart, List = startNext || breakAtStart ? first : null });
                result.AddRange(parts.Skip(1).Select(x => x with { NewParagraph = true }));
                startNext = breakAtEnd;
                nextList = breakAtEnd ? endList : null;
            }
            else
            {
                if (breakAtEnd) nextList = endList; else if (breakAtStart) nextList = startList;
                startNext = startNext || breakAtStart || breakAtEnd;
            }
            applied += n;
        }
        return (result, applied, heard);
    }

    /// <summary>
    /// Whether the speech starts with "new paragraph" (or "bullet point" and the like). The first passage always starts a
    /// paragraph, so this is how the Widget knows to put a break between text already in the box and what was said next.
    /// </summary>
    public static bool StartsWithBreak(IEnumerable<string> texts)
    {
        var raw = MarkLineBreaks(string.Join(' ', texts.Take(3))).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (raw.Length == 0) return false;
        if (raw[0] == LineBreak) return true;
        return Commands.FirstOrDefault(c => Matches(raw, 0, c.Words)) is { Kind: Kind.Break or Kind.ListPoint };
    }

    public static (IReadOnlyList<Segment> Segments, int Applied) Apply(Segment s)
    {
        var (parts, n, _, _, _, _) = Run(s, null, null, KeepCapitals([s], null), new Flow(false));
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
    /// <summary>"End list" as the speech service sometimes writes it; only taken as the command while a list point is open.</summary>
    private static readonly Command Endless = new(["endless"], Kind.Break);
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

    private static string NameOf(Command c) => c == SpeechServiceBreak ? "line break from the speech service"
        : c == Endless ? "end list (heard as endless)" : string.Join(' ', c.Words);

    /// <summary>
    /// Carried from passage to passage. With spoken punctuation only, the speech service's own . , ? ! ; : are taken away
    /// (the person says the punctuation they want), and a capital it gave after its own sentence end is taken away too.
    /// </summary>
    private sealed class Flow(bool spokenOnly)
    {
        public readonly bool SpokenOnly = spokenOnly;
        /// <summary>The speech service ended a sentence on the last word and that ending was taken away.</summary>
        public bool LowerNext;
        /// <summary>The paragraph being written is a bullet or numbered point.</summary>
        public bool InList;
        /// <summary>No word written yet: a comma or colon said first belongs after text already in the box (Widget, Speak more).</summary>
        public bool NothingYet = true;
    }

    public const string RemovedPunctuation = "punctuation from the speech service removed";
    public const string RemovedCapital = "capital from the speech service removed";
    /// <summary>Kept with their full stop: they are abbreviations, not sentence ends.</summary>
    private static readonly HashSet<string> Abbreviations = ["mr", "mrs", "ms", "dr", "st", "prof", "etc", "vs", "no", "jr", "sr", "co", "ltd", "inc", "esq"];

    /// <summary>The word without the punctuation the speech service put at its end; "e.g.", "U.K." and "Mr." keep their full stop.</summary>
    private static string WithoutServicePunctuation(string text)
    {
        // Closing quotes or brackets the service wrote stay; only the punctuation in front of them goes.
        var close = text.Length;
        while (close > 0 && text[close - 1] is '"' or ')' or '\'' or '’' or '”') close--;
        var end = close;
        while (end > 0 && Trailing.Contains(text[end - 1])) end--;
        if (end == close || end == 0) return text;
        var body = text[..end];
        if (text[end] == '.' && end + 1 == close && (body.Contains('.') || Abbreviations.Contains(Bare(body)))) return text;
        return body + text[close..];
    }

    private static (List<Segment> Segments, int Applied, bool BreakAtStart, bool BreakAtEnd, string? StartList, string? EndList) Run(Segment s, IDictionary<string, int>? heard, Inserts? inserts, HashSet<string> keep, Flow flow)
    {
        var touched = false;
        var raw = MarkLineBreaks(s.Text).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        // Word timings line up with the spoken words, not with line breaks.
        var wordAt = new int[raw.Length];
        for (int i = 0, n = 0; i < raw.Length; i++) wordAt[i] = raw[i] == LineBreak ? -1 : n++;
        var timed = s.Words is { } w && w.Count == raw.Count(x => x != LineBreak) ? w : null;
        var paragraphs = new List<List<Token>> { new() };
        var lists = new List<string?> { null }; // the list point each paragraph starts, if any
        var applied = 0;
        var capitalNext = false;
        var lowerNext = false;
        var afterComma = false;
        var breakAtStart = false;
        string? openNext = null;
        for (var i = 0; i < raw.Length;)
        {
            if (inserts is not null && raw[i] != LineBreak && InsertWords.Contains(Bare(raw[i])))
            {
                if (FindInsert(raw, i + 1, inserts) is { } hit)
                {
                    applied++;
                    lowerNext = false; afterComma = false;
                    Count(heard, "insert " + hit.Insert.Code);
                    // The inserted words share out the time it took to say "insert legal 1.1", so highlighting passes over them then.
                    var from = timed?[wordAt[i]].StartMs ?? 0;
                    var to = timed?[wordAt[i + hit.Length]].EndMs ?? 0;
                    var total = hit.Insert.Paragraphs.Sum(p => p.Length);
                    var n = 0;
                    for (var p = 0; p < hit.Insert.Paragraphs.Count; p++)
                    {
                        if (p > 0 && paragraphs[^1].Count > 0) { paragraphs.Add([]); lists.Add(null); }
                        foreach (var word in hit.Insert.Paragraphs[p])
                        {
                            var text = word;
                            if (openNext is not null) { text = openNext + text; openNext = null; }
                            if (capitalNext) { text = Capitalise(text); capitalNext = false; }
                            WordTiming? timing = timed is null ? null
                                : new(text, from + (int)((long)(to - from) * n / total), from + (int)((long)(to - from) * (n + 1) / total));
                            paragraphs[^1].Add(new Token(text, timing, i, hit.Insert.Code));
                            flow.NothingYet = false;
                            n++;
                        }
                    }
                    flow.LowerNext = false;
                    i += 1 + hit.Length;
                    continue;
                }
                if (i + 1 < raw.Length && inserts.FirstWords.Contains(SpokenCodes.Key([raw[i + 1]]).Split(' ')[0])) Count(heard, NoMatch);
            }
            if (raw[i] != LineBreak && Matches(raw, i, ScratchThat))
            {
                applied++;
                Count(heard, "scratch that");
                capitalNext = ScratchLast(paragraphs, lists) || capitalNext;
                lowerNext = false; afterComma = false; openNext = null; flow.LowerNext = false;
                i += 2;
                continue;
            }
            if (raw[i] != LineBreak && ReplaceWords.ContainsKey(Bare(raw[i])) && FindReplacement(raw, i, timed, wordAt) is { } rep)
            {
                if (Replace(paragraphs, rep.Old, rep.New, flow.SpokenOnly))
                {
                    applied++;
                    Count(heard, "replace");
                    lowerNext = false; afterComma = false;
                    i = rep.End;
                    continue;
                }
                Count(heard, ReplaceNotFound);
            }
            var cmd = raw[i] == LineBreak ? SpeechServiceBreak : Commands.FirstOrDefault(c => Matches(raw, i, c.Words))
                ?? (flow.InList && Matches(raw, i, Endless.Words) ? Endless : null);
            var current = paragraphs[^1];
            var prev = current.Count > 0 ? current[^1] : null;
            if (cmd is null)
            {
                var text = raw[i];
                if (lowerNext)
                {
                    var lowered = Uncapitalise(text, keep);
                    if (lowered != text) Count(heard, LoweredName);
                    text = lowered; lowerNext = false;
                }
                // Counted so the history shows when a capital after a comma was left alone (a name, or no sentence end seen).
                if (afterComma && text.FirstOrDefault(char.IsLetter) is var first && char.IsUpper(first)) Count(heard, KeptName);
                afterComma = false;
                if (openNext is not null) { text = openNext + text; openNext = null; }
                if (capitalNext) { text = Capitalise(text); capitalNext = false; flow.LowerNext = false; }
                if (flow.SpokenOnly)
                {
                    if (flow.LowerNext)
                    {
                        var lowered = Uncapitalise(text, keep);
                        if (lowered != text) { Count(heard, RemovedCapital); touched = true; text = lowered; }
                    }
                    var bare = WithoutServicePunctuation(text);
                    flow.LowerNext = bare != text && EndsSentence(text);
                    if (bare != text) { Count(heard, RemovedPunctuation); touched = true; text = bare; }
                }
                current.Add(new Token(text, timed?[wordAt[i]], i));
                flow.NothingYet = false;
                i++;
                continue;
            }
            applied++;
            lowerNext = false; afterComma = false; // only ever the word straight after the comma
            // A spoken comma after the service's own sentence end keeps the next word small; other commands decide for themselves.
            var serviceEnded = flow.LowerNext;
            if (cmd.Kind != Kind.Open) flow.LowerNext = false;
            i += cmd.Words.Length;
            Count(heard, NameOf(cmd));
            switch (cmd.Kind)
            {
                case Kind.Break:
                case Kind.ListPoint:
                    // "bullet point" and "numbered point" start a paragraph that is a list point; "new paragraph" and "end list" an ordinary one.
                    if (current.Count > 0) { paragraphs.Add([]); lists.Add(null); }
                    else if (paragraphs.Count == 1) breakAtStart = true;
                    lists[^1] = cmd.Kind == Kind.ListPoint ? cmd.Symbol : null;
                    flow.InList = cmd.Kind == Kind.ListPoint;
                    capitalNext = true;
                    break;
                case Kind.Punctuation:
                    // The speech service may have ended a sentence here, before the command ("2023. Comma. Please") or on the
                    // command word itself ("2023 comma. Please"); the comma takes the full stop away, so the capital goes too.
                    lowerNext = cmd.Symbol is "," or ":" or ";" && prev is not null
                        && (serviceEnded || EndsSentence(prev.Text) || raw.Skip(i - cmd.Words.Length).Take(cmd.Words.Length).Any(EndsSentence));
                    afterComma = cmd.Symbol is "," or ":" or ";";
                    if (prev is not null) prev.Text = prev.Text.TrimEnd(Trailing.ToCharArray()) + cmd.Symbol;
                    else if (flow.NothingYet && paragraphs.Count == 1)
                    {
                        // Said before any word: kept on its own, to join the text the person already has.
                        var at = wordAt[i - cmd.Words.Length];
                        current.Add(new Token(cmd.Symbol, timed is null || at < 0 ? null : timed[at] with { Text = cmd.Symbol }, i - cmd.Words.Length));
                    }
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
        foreach (var para in paragraphs)
            for (var joined = JoinEmailAddresses(para); joined > 0; joined--) { Count(heard, EmailJoined); touched = true; }
        if (applied == 0 && !touched) return ([s], 0, false, false, null, null);

        var breakAtEnd = paragraphs.Count > 1 && paragraphs[^1].Count == 0;
        var keptAt = Enumerable.Range(0, paragraphs.Count).Where(p => paragraphs[p].Count > 0).ToList();
        var kept = keptAt.Select(p => paragraphs[p]).ToList();
        if (kept.Count == 0) return ([], applied, breakAtStart, false, lists[0], null); // the passage was nothing but commands
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
                Inserted = codes.Count == 0 ? null : string.Join(", ", codes),
                List = lists[keptAt[p]]
            });
        }
        return (segments, applied, breakAtStart, breakAtEnd, lists[0], breakAtEnd ? lists[^1] : null);
    }

    // ---------- correcting by voice ----------

    public const string ReplaceNotFound = "replace, words not found";
    /// <summary>"replace X with Y" and "correct X to Y".</summary>
    private static readonly Dictionary<string, string> ReplaceWords = new() { ["replace"] = "with", ["correct"] = "to" };
    private const int MaxReplaceWords = 6;
    /// <summary>Not "delete that": "please delete that file" is ordinary speech.</summary>
    private static readonly string[] ScratchThat = ["scratch", "that"];
    /// <summary>A pause this long after the new words ends them ("replace Tuesday with Thursday ... and then").</summary>
    private const int ReplacePauseMs = 350;

    /// <summary>
    /// "scratch that": takes away what was said since the last punctuation mark (in the paragraph being written), or,
    /// said straight after one, the words before it back to the mark before that. Saved text put in with
    /// "insert" goes as a whole. Returns whether the next word starts a sentence.
    /// </summary>
    private static bool ScratchLast(List<List<Token>> paragraphs, List<string?> lists)
    {
        // Nothing in the paragraph being written: the last thing said is at the end of the paragraph before.
        while (paragraphs.Count > 1 && paragraphs[^1].Count == 0) { paragraphs.RemoveAt(paragraphs.Count - 1); lists.RemoveAt(lists.Count - 1); }
        var tokens = paragraphs[^1];
        if (tokens.Count == 0) return true;
        var cut = tokens.Count;
        if (tokens[^1].Insert is { } code)
            while (cut > 0 && tokens[cut - 1].Insert == code) cut--;
        else
        {
            cut--; // the last word goes, even when it ends a sentence
            while (cut > 0 && !EndsPhrase(tokens[cut - 1].Text) && tokens[cut - 1].Insert is null) cut--;
        }
        tokens.RemoveRange(cut, tokens.Count - cut);
        return cut == 0 || EndsSentence(tokens[cut - 1].Text);
    }

    private static bool EndsPhrase(string word) => word.TrimEnd('"', ')', '\'', '’', '”') is { Length: > 0 } w && Trailing.Contains(w[^1]);

    private sealed record Replacement(string[] Old, string[] New, int End);

    /// <summary>"replace" + old words + "with" + new words, the new words ending at a pause, a command, a sentence end or the passage end.</summary>
    private static Replacement? FindReplacement(string[] raw, int at, IReadOnlyList<WordTiming>? timed, int[] wordAt)
    {
        var joiner = ReplaceWords[Bare(raw[at])];
        for (var k = at + 2; k <= Math.Min(raw.Length - 2, at + 1 + MaxReplaceWords); k++)
        {
            if (raw[k] == LineBreak) return null;
            if (Bare(raw[k]) != joiner) continue;
            var end = k + 1;
            while (end < raw.Length && end - (k + 1) < MaxReplaceWords && raw[end] != LineBreak
                   && !Commands.Any(c => Matches(raw, end, c.Words)) && !Matches(raw, end, ScratchThat))
            {
                end++;
                if (EndsSentence(raw[end - 1])) break;
                if (timed is not null && end < raw.Length && raw[end] != LineBreak && wordAt[end] >= 0 && wordAt[end - 1] >= 0
                    && timed[wordAt[end]].StartMs - timed[wordAt[end - 1]].EndMs >= ReplacePauseMs) break;
            }
            if (end == k + 1) return null;
            // Without timings or a clear end, only short replacements count, so ordinary speech is left alone.
            if (end - (k + 1) >= MaxReplaceWords && end < raw.Length) return null;
            var old = raw[(at + 1)..k].Select(Bare).Where(w => w.Length > 0).ToArray();
            var replacement = raw[(k + 1)..end].Select(w => w.Trim()).ToArray();
            return old.Length == 0 ? null : new Replacement(old, replacement, end);
        }
        return null;
    }

    /// <summary>Changes the last place the old words were said (in one paragraph) to the new words. False when they were not found.</summary>
    private static bool Replace(List<List<Token>> paragraphs, string[] old, string[] words, bool spokenOnly)
    {
        for (var p = paragraphs.Count - 1; p >= 0; p--)
        {
            var tokens = paragraphs[p];
            for (var at = tokens.Count - old.Length; at >= 0; at--)
            {
                if (!Enumerable.Range(0, old.Length).All(n => Bare(tokens[at + n].Text) == old[n])) continue;
                var first = tokens[at];
                var lastText = tokens[at + old.Length - 1].Text;
                // Punctuation around the old words stays: an opening quote or bracket before, a comma or full stop after.
                var lead = new string(first.Text.TakeWhile(ch => ch is '"' or '(' or '\'' or '‘' or '“').ToArray());
                var core = lastText.TrimEnd('"', ')', '\'', '’', '”');
                var tail = lastText[core.TrimEnd(Trailing.ToCharArray()).Length..];
                var fresh = words.Select(w => spokenOnly ? WithoutServicePunctuation(w) : w).Select(w => w.TrimEnd(Trailing.ToCharArray()))
                    .Where(w => w.Length > 0).ToList();
                if (fresh.Count == 0) return false;
                var startsCapital = first.Text.FirstOrDefault(char.IsLetter) is var f && char.IsUpper(f);
                fresh[0] = startsCapital ? Capitalise(fresh[0]) : fresh[0];
                fresh[0] = lead + fresh[0];
                fresh[^1] += tail;
                // The new words share out the time the old ones took, so highlighting still follows the recording.
                var from = first.Timing?.StartMs ?? 0;
                var to = tokens[at + old.Length - 1].Timing?.EndMs ?? 0;
                var made = fresh.Select((w, n) => new Token(w, first.Timing is null ? null
                    : first.Timing with { Text = w, StartMs = from + (to - from) * n / fresh.Count, EndMs = from + (to - from) * (n + 1) / fresh.Count }, first.Index)).ToList();
                tokens.RemoveRange(at, old.Length);
                tokens.InsertRange(at, made);
                return true;
            }
        }
        return false;
    }

    public const string EmailJoined = "email address joined";
    private static readonly HashSet<string> EmailSeparators = ["dot", "underscore", "dash", "hyphen"];
    private static string SeparatorSymbol(string word) => word switch { "dot" => ".", "underscore" => "_", _ => "-" };
    /// <summary>Exactly this word, in any case, with nothing attached.</summary>
    private static bool IsWord(string text, string word) => string.Equals(text, word, StringComparison.OrdinalIgnoreCase);
    private static bool IsAddressPart(string text) => text.Length > 0 && text.All(ch => char.IsLetterOrDigit(ch) || ch is '.' or '_' or '-');

    /// <summary>
    /// A spoken email address ("ashley dot lipke at btinternet dot com", or as the speech service often writes it,
    /// "Ashley dot Lipke @btinternet.com") made into one word: ashley.lipke@btinternet.com. Needs an "@", or a spoken
    /// "at" after a spoken "dot", with a domain containing a dot after it, so ordinary speech is left alone. Returns how
    /// many addresses were joined.
    /// </summary>
    private static int JoinEmailAddresses(List<Token> tokens)
    {
        var count = 0;
        for (var j = 0; j < tokens.Count; j++)
        {
            var word = tokens[j].Text;
            var core = word.TrimEnd(Trailing.ToCharArray());
            var tail = word[core.Length..]; // a spoken comma or full stop after the address stays after it
            string local, domain;
            int from, to;
            var atSign = core.IndexOf('@');
            if (atSign >= 0)
            {
                local = core[..atSign]; domain = core[(atSign + 1)..];
                if (local.Length > 0 && !IsAddressPart(local) || domain.Length > 0 && !IsAddressPart(domain)) continue;
            }
            else if (IsWord(word, "at") && j > 0 && j + 1 < tokens.Count) { local = ""; domain = ""; }
            else continue;

            // Backwards: the words of the part before the @, joined by spoken "dot", "underscore" or "dash".
            from = j;
            var localWords = new List<string>();
            if (local.Length == 0)
            {
                var k = j - 1;
                if (k < 0 || !IsAddressPart(tokens[k].Text)) continue;
                localWords.Add(tokens[k].Text); from = k;
            }
            while (from >= 2 && EmailSeparators.Any(sep => IsWord(tokens[from - 1].Text, sep)) && IsAddressPart(tokens[from - 2].Text))
            {
                localWords.Insert(0, SeparatorSymbol(Bare(tokens[from - 1].Text)));
                localWords.Insert(0, tokens[from - 2].Text);
                from -= 2;
            }
            local = string.Concat(localWords) + local;

            // Forwards: the domain, with spoken "dot"s.
            to = j;
            if (domain.Length == 0)
            {
                if (to + 1 >= tokens.Count) continue;
                var d = tokens[to + 1].Text.TrimEnd(Trailing.ToCharArray());
                if (!IsAddressPart(d)) continue;
                domain = d; tail = tokens[to + 1].Text[d.Length..]; to++;
            }
            while (tail.Length == 0 && to + 2 < tokens.Count && IsWord(tokens[to + 1].Text, "dot"))
            {
                var d = tokens[to + 2].Text.TrimEnd(Trailing.ToCharArray());
                if (!IsAddressPart(d)) break;
                domain += "." + d; tail = tokens[to + 2].Text[d.Length..]; to += 2;
            }
            if (local.Length == 0 || !domain.Contains('.') || domain.StartsWith('.') || domain.EndsWith('.')) continue;
            // A spoken "at" (not "@") only counts with a spoken "dot" before it, so "find it at example.com" stays as said.
            if (atSign < 0 && localWords.Count < 3) continue;
            if (from == j && to == j && atSign >= 0 && localWords.Count == 0 && core == core.ToLowerInvariant()) continue; // already one word

            var first = tokens[from].Timing;
            var last = tokens[to].Timing;
            var text = (local + "@" + domain).ToLowerInvariant() + tail;
            var timing = first is null ? null : first with { Text = text, EndMs = last?.EndMs ?? first.EndMs };
            tokens[from] = new Token(text, timing, tokens[from].Index, tokens[from].Insert);
            tokens.RemoveRange(from + 1, to - from);
            j = from;
            count++;
        }
        return count;
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

    public const string LoweredName = "small letter after a spoken comma";
    public const string KeptName = "capital kept after a spoken comma";
    /// <summary>The last word of every spoken command: a capital straight after one was given by the speech service, not a name.</summary>
    private static readonly HashSet<string> CommandEnds = Commands.Select(c => c.Words[^1]).Append(Endless.Words[0]).ToHashSet();

    private static bool EndsSentence(string word) => word.TrimEnd('"', ')', '\'', '’', '”') is { Length: > 0 } w && w[^1] is '.' or '?' or '!';

    /// <summary>
    /// Words that keep their capital after a comma: "I" and its forms, names in the custom vocabulary, words in capitals
    /// (BBC), and any word the speech service capitalised in the middle of a sentence somewhere in the dictation (a name).
    /// </summary>
    private static HashSet<string> KeepCapitals(IEnumerable<Segment> segments, IEnumerable<string>? names)
    {
        var keep = new HashSet<string>(StringComparer.Ordinal) { "i", "i'm", "i'll", "i've", "i'd", "i’m", "i’ll", "i’ve", "i’d" };
        foreach (var n in names ?? [])
            foreach (var w in n.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)) keep.Add(Bare(w));
        foreach (var seg in segments)
        {
            var words = MarkLineBreaks(seg.Text).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            for (var i = 1; i < words.Length; i++)
            {
                if (words[i - 1] == LineBreak || EndsSentence(words[i - 1]) || CommandEnds.Contains(Bare(words[i - 1]))) continue;
                var w = words[i].TrimStart('"', '(', '\'', '‘', '“');
                if (w.Length > 0 && char.IsUpper(w[0])) keep.Add(Bare(w));
            }
        }
        return keep;
    }

    private static string Uncapitalise(string text, HashSet<string> keep)
    {
        var i = 0;
        while (i < text.Length && !char.IsLetter(text[i])) i++;
        if (i >= text.Length || !char.IsUpper(text[i])) return text;
        if (keep.Contains(Bare(text))) return text;
        var letters = text.Where(char.IsLetter).ToArray();
        if (letters.Length > 1 && letters.All(char.IsUpper)) return text; // BBC, NHS
        return text[..i] + char.ToLower(text[i], CultureInfo.InvariantCulture) + text[(i + 1)..];
    }

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
