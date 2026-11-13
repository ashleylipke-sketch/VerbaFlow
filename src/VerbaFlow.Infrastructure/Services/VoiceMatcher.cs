using VerbaFlow.Core.Providers;
using VerbaFlow.Core.Transcripts;

namespace VerbaFlow.Infrastructure.Services;

/// <summary>
/// Names a meeting's speakers from the voices people saved: each speaker's sentences are fingerprinted, averaged, and
/// compared with the saved voices. A speaker gets a person's name only when they sound clearly like that person and
/// clearly more like them than like anyone else; each person names one speaker at most. Speakers who match nobody keep
/// "Speaker N".
/// </summary>
public static class VoiceMatcher
{
    /// <summary>A saved voice to compare with: the person's id, their name and their fingerprint.</summary>
    public sealed record Known(Guid PersonId, string Name, float[] Print);

    /// <param name="Compared">Speakers with enough fingerprinted speech to compare (at least <see cref="MinSpeakerMs"/>).</param>
    public sealed record Outcome(IReadOnlyDictionary<Guid, Known> Named, int Compared);

    /// <summary>How alike (cosine) a speaker and a saved voice must be. Same voices usually score well above, different ones below.</summary>
    public const float Threshold = 0.45f;
    /// <summary>How much more alike the chosen person must be than the next best one, so two similar voices are not confused.</summary>
    public const float Margin = 0.08f;
    /// <summary>A speaker with less fingerprinted speech than this is not compared.</summary>
    public const int MinSpeakerMs = 3000;

    /// <summary>Each speaker's voice: the average of their pieces' fingerprints, weighted by length.</summary>
    public static Dictionary<Guid, float[]> SpeakerVoices(IReadOnlyList<IntroductionMatcher.Piece> pieces, IReadOnlyList<float[]?> prints)
    {
        var sums = new Dictionary<Guid, (float[] Sum, int Ms)>();
        for (var i = 0; i < pieces.Count && i < prints.Count; i++)
        {
            if (prints[i] is not { Length: > 0 } p) continue;
            var v = Normalise(p);
            var ms = pieces[i].EndMs - pieces[i].StartMs;
            var (sum, total) = sums.TryGetValue(pieces[i].SpeakerId, out var s) ? s : (new float[v.Length], 0);
            for (var d = 0; d < Math.Min(sum.Length, v.Length); d++) sum[d] += v[d] * ms;
            sums[pieces[i].SpeakerId] = (sum, total + ms);
        }
        return sums.Where(x => x.Value.Ms >= MinSpeakerMs).ToDictionary(x => x.Key, x => Normalise(x.Value.Sum));
    }

    /// <summary>Which speaker is which saved voice: the most alike pairs first, each person and each speaker used once.</summary>
    public static Outcome Match(IReadOnlyDictionary<Guid, float[]> speakers, IReadOnlyList<Known> known)
    {
        var named = new Dictionary<Guid, Known>();
        if (known.Count == 0 || speakers.Count == 0) return new Outcome(named, speakers.Count);
        var pairs = new List<(Guid Speaker, int Person, float Sim)>();
        foreach (var (speaker, voice) in speakers)
        {
            var sims = known.Select(k => Dot(voice, Normalise(k.Print))).ToArray();
            for (var p = 0; p < known.Count; p++)
            {
                var next = sims.Where((_, i) => i != p).DefaultIfEmpty(float.NegativeInfinity).Max();
                if (sims[p] >= Threshold && sims[p] - next >= Margin) pairs.Add((speaker, p, sims[p]));
            }
        }
        var taken = new HashSet<int>();
        foreach (var (speaker, person, _) in pairs.OrderByDescending(x => x.Sim))
        {
            if (named.ContainsKey(speaker) || taken.Contains(person)) continue;
            named[speaker] = known[person]; taken.Add(person);
        }
        return new Outcome(named, speakers.Count);
    }

    /// <summary>The speakers renamed after the people they matched (two people with the same name keep apart by a number).</summary>
    public static TranscriptionResult Rename(TranscriptionResult result, IReadOnlyDictionary<Guid, Known> named)
    {
        var used = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var speakers = result.Speakers.Select(s =>
        {
            if (!named.TryGetValue(s.Id, out var k)) return s;
            var n = used[k.Name] = used.GetValueOrDefault(k.Name) + 1;
            return s with { Label = n == 1 ? k.Name : $"{k.Name} ({n})" };
        }).ToList();
        return result with { Speakers = speakers };
    }

    /// <summary>One fingerprint for a whole sample: the average of its pieces.</summary>
    public static float[]? Average(IReadOnlyList<float[]?> prints)
    {
        var good = prints.Where(p => p is { Length: > 0 }).Select(p => Normalise(p!)).ToList();
        if (good.Count == 0) return null;
        return Normalise(Enumerable.Range(0, good[0].Length).Select(d => good.Sum(v => d < v.Length ? v[d] : 0)).ToArray());
    }

    private static float Dot(float[] a, float[] b) { var s = 0f; for (var i = 0; i < Math.Min(a.Length, b.Length); i++) s += a[i] * b[i]; return s; }
    public static float[] Normalise(float[] v)
    {
        var n = MathF.Sqrt(v.Sum(x => x * x));
        return n == 0 ? v : v.Select(x => x / n).ToArray();
    }
}
