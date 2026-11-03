using VerbaFlow.Core.Domain;
using VerbaFlow.Core.Providers;
using VerbaFlow.Core.Transcripts;

namespace VerbaFlow.Infrastructure.Services;

/// <summary>
/// Uses the introductions at the start of an in-room meeting (each person saying their name, one at a time) to name the
/// speakers and, where voice fingerprints are available, to put each sentence with the person whose voice it sounds like.
/// </summary>
public static class IntroductionMatcher
{
    /// <summary>A stretch of one box, usually one sentence, that is checked against the voice samples as a whole.</summary>
    public sealed record Piece(int StartMs, int EndMs, Guid SpeakerId);

    /// <param name="Turns">Who spoke each piece: a person's number (0, 1, ...) or, for a voice that matched nobody, 1000 + its own number.</param>
    /// <param name="Named">How many of the speech service's speakers were given an introduced person's name.</param>
    /// <param name="Moved">How many pieces went to a different person than the speech service had put them with.</param>
    public sealed record Outcome(IReadOnlyList<SpeakerTurn> Turns, IReadOnlyDictionary<int, string> Names, int Named, int Moved, int Checked);

    /// <summary>Pieces shorter than this are too short for a reliable voice fingerprint and stay where they are.</summary>
    public const int MinPieceMs = 1200;
    /// <summary>A long sentence with no full stop is cut at the next word after this long.</summary>
    public const int MaxPieceMs = 12000;
    /// <summary>A piece moves to another person only when it sounds this much more like them (cosine similarity).</summary>
    public const float Margin = 0.08f;
    /// <summary>A voice that matched nobody's introduction goes to a person only when it sounds at least this much like them.</summary>
    public const float Floor = 0.35f;

    /// <summary>
    /// Each speaker the speech service found is given the person whose introduction they spoke the most of. Each person and
    /// each speaker is used once, biggest overlaps first. Returns speaker id → person number (an index into <paramref name="people"/>).
    /// </summary>
    public static Dictionary<Guid, int> NameSpeakers(TranscriptionResult result, IReadOnlyList<Introduction> intros, IReadOnlyList<string> people)
    {
        var overlap = new Dictionary<(Guid Speaker, int Person), int>();
        foreach (var seg in result.Segments)
            foreach (var (start, end) in Spans(seg))
                foreach (var intro in intros)
                {
                    var o = Math.Min(end, intro.EndMs) - Math.Max(start, intro.StartMs);
                    if (o <= 0) continue;
                    var key = (seg.SpeakerId, PersonOf(people, intro.Name));
                    overlap[key] = overlap.GetValueOrDefault(key) + o;
                }
        var named = new Dictionary<Guid, int>();
        var taken = new HashSet<int>();
        foreach (var ((speaker, person), ms) in overlap.OrderByDescending(x => x.Value))
        {
            if (ms < 200 || named.ContainsKey(speaker) || taken.Contains(person)) continue;
            named[speaker] = person; taken.Add(person);
        }
        return named;
    }

    /// <summary>Cuts every box into sentences (by the word timings) so a box holding two voices can be shared out.</summary>
    public static List<Piece> Pieces(TranscriptionResult result)
    {
        var pieces = new List<Piece>();
        foreach (var seg in result.Segments.OrderBy(s => s.StartMs))
        {
            var words = SpeakerAligner.Tokens(seg);
            if (words is null) { pieces.Add(new Piece(seg.StartMs, seg.EndMs, seg.SpeakerId)); continue; }
            var from = 0;
            for (var i = 0; i < words.Count; i++)
            {
                var length = words[i].End - words[from].Start;
                var bare = words[i].Text.TrimEnd('"', '\'', ')', '”', '’');
                var sentenceEnd = bare.EndsWith('.') || bare.EndsWith('?') || bare.EndsWith('!');
                if (i == words.Count - 1 || (sentenceEnd && length >= MinPieceMs) || length >= MaxPieceMs)
                {
                    pieces.Add(new Piece(words[from].Start, words[i].End, seg.SpeakerId));
                    from = i + 1;
                }
            }
        }
        return pieces;
    }

    /// <summary>
    /// Decides who said each piece. A piece inside someone's introduction is theirs. Otherwise it stays with the person its
    /// speaker was named after, unless its voice fingerprint is clearly closer to another person's introduction.
    /// </summary>
    /// <param name="piecePrints">One fingerprint per piece (null where none), or null when fingerprints are not available.</param>
    /// <param name="introPrints">One fingerprint per introduction (null where none).</param>
    public static Outcome Decide(TranscriptionResult result, IReadOnlyList<Introduction> intros, IReadOnlyList<string> people,
        IReadOnlyDictionary<Guid, int> named, IReadOnlyList<Piece> pieces, IReadOnlyList<float[]?>? piecePrints, IReadOnlyList<float[]?>? introPrints)
    {
        // Each person's sample is the average of their introductions' fingerprints.
        var samples = new float[]?[people.Count];
        if (introPrints is not null)
            for (var p = 0; p < people.Count; p++)
            {
                var mine = intros.Select((x, i) => (x, i)).Where(t => PersonOf(people, t.x.Name) == p)
                    .Select(t => introPrints[t.i]).Where(v => v is { Length: > 0 }).Select(v => Normalise(v!)).ToList();
                if (mine.Count > 0) samples[p] = Normalise(Enumerable.Range(0, mine[0].Length).Select(d => mine.Sum(v => v[d])).ToArray());
            }

        var speakerNo = result.Speakers.Select((s, i) => (s.Id, i)).ToDictionary(x => x.Id, x => x.i);
        var turns = new List<SpeakerTurn>();
        int moved = 0, checkedPieces = 0;
        for (var k = 0; k < pieces.Count; k++)
        {
            var piece = pieces[k];
            var current = named.TryGetValue(piece.SpeakerId, out var p0) ? p0 : -1;
            var who = current;
            var inIntro = intros.Select(x => (x.Name, O: Math.Min(piece.EndMs, x.EndMs) - Math.Max(piece.StartMs, x.StartMs)))
                .Where(x => x.O * 2 >= piece.EndMs - piece.StartMs && x.O > 0).Select(x => (string?)x.Name).FirstOrDefault();
            if (inIntro is not null) who = PersonOf(people, inIntro);
            else if (piecePrints?[k] is { Length: > 0 } print && samples.Any(s => s is not null))
            {
                checkedPieces++;
                var v = Normalise(print);
                var sims = samples.Select(s => s is null ? float.NegativeInfinity : Dot(v, s)).ToArray();
                var best = Array.IndexOf(sims, sims.Max());
                if (current < 0) { if (sims[best] >= Floor) who = best; }
                else if (best != current && (samples[current] is null || sims[best] - sims[current] >= Margin)) who = best;
            }
            if (who != current && (current >= 0 || who >= 0)) moved++;
            turns.Add(new SpeakerTurn(piece.StartMs, piece.EndMs, who >= 0 ? who : 1000 + speakerNo.GetValueOrDefault(piece.SpeakerId)));
        }
        var names = people.Select((n, i) => (n, i)).ToDictionary(x => x.i, x => x.n);
        return new Outcome(turns, names, named.Count, moved, checkedPieces);
    }

    /// <summary>The person's number for a name (capitals ignored, so the same name twice is one person).</summary>
    public static int PersonOf(IReadOnlyList<string> people, string name)
    {
        for (var i = 0; i < people.Count; i++) if (string.Equals(people[i], name.Trim(), StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    private static IEnumerable<(int Start, int End)> Spans(Segment seg) =>
        seg.Words is { Count: > 0 } w ? w.Select(x => (x.StartMs, x.EndMs)) : [(seg.StartMs, seg.EndMs)];

    private static float Dot(float[] a, float[] b) { var s = 0f; for (var i = 0; i < Math.Min(a.Length, b.Length); i++) s += a[i] * b[i]; return s; }
    private static float[] Normalise(float[] v)
    {
        var n = MathF.Sqrt(v.Sum(x => x * x));
        return n == 0 ? v : v.Select(x => x / n).ToArray();
    }
}
