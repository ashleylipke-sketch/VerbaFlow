namespace VerbaFlow.Infrastructure.Services;

/// <summary>
/// Groups short voice fingerprints (one per ~1.5 s window of speech) into speakers. Average-linkage agglomerative clustering
/// on cosine distance. With a known count it stops at that many groups; otherwise it looks for the biggest jump in
/// merge distance, which is where genuinely different voices start being joined.
/// </summary>
public static class VoiceClusterer
{
    public const int MinAutoSpeakers = 2, MaxAutoSpeakers = 8;

    /// <returns>One group number (0-based, in order of first appearance) per fingerprint.</returns>
    public static int[] Cluster(IReadOnlyList<float[]> embeddings, int? speakers)
    {
        var n = embeddings.Count;
        if (n == 0) return [];
        if (n == 1) return [0];
        var unit = embeddings.Select(Normalise).ToArray();

        var dist = new float[n, n];
        for (var i = 0; i < n; i++)
            for (var j = i + 1; j < n; j++)
            {
                float dot = 0; var a = unit[i]; var b = unit[j];
                for (var k = 0; k < a.Length; k++) dot += a[k] * b[k];
                dist[i, j] = dist[j, i] = 1f - dot;
            }

        var members = Enumerable.Range(0, n).Select(i => new List<int> { i }).ToArray();
        var active = Enumerable.Repeat(true, n).ToArray();
        var size = Enumerable.Repeat(1, n).ToArray();
        var nearest = new int[n]; var nearestD = new float[n];
        void Refresh(int i)
        {
            nearest[i] = -1; nearestD[i] = float.MaxValue;
            for (var j = 0; j < n; j++)
                if (j != i && active[j] && dist[i, j] < nearestD[i]) { nearestD[i] = dist[i, j]; nearest[i] = j; }
        }
        for (var i = 0; i < n; i++) Refresh(i);

        var merges = new List<(int A, int B, float D)>();
        for (var step = 0; step < n - 1; step++)
        {
            int ba = -1; var bd = float.MaxValue;
            for (var i = 0; i < n; i++) if (active[i] && nearest[i] >= 0 && nearestD[i] < bd) { bd = nearestD[i]; ba = i; }
            var bb = nearest[ba];
            merges.Add((ba, bb, bd));
            // average linkage: weighted mean of the two old distances
            for (var k = 0; k < n; k++)
            {
                if (!active[k] || k == ba || k == bb) continue;
                var d = (dist[ba, k] * size[ba] + dist[bb, k] * size[bb]) / (size[ba] + size[bb]);
                dist[ba, k] = dist[k, ba] = d;
            }
            size[ba] += size[bb]; members[ba].AddRange(members[bb]); active[bb] = false;
            for (var k = 0; k < n; k++)
                if (active[k] && (k == ba || nearest[k] == ba || nearest[k] == bb)) Refresh(k);
        }

        var target = speakers is { } s ? Math.Clamp(s, 1, n) : ChooseCount(merges.Select(m => m.D).ToList(), n);
        // Replay the first n - target merges.
        var parent = Enumerable.Range(0, n).ToArray();
        int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
        for (var i = 0; i < n - target; i++) parent[Find(merges[i].B)] = Find(merges[i].A);

        var map = new Dictionary<int, int>();
        var labels = new int[n];
        for (var i = 0; i < n; i++) { var r = Find(i); if (!map.TryGetValue(r, out var g)) map[r] = g = map.Count; labels[i] = g; }
        return labels;
    }

    /// <summary>The number of groups just before the largest jump in merge distance, kept within a sensible range.</summary>
    internal static int ChooseCount(IReadOnlyList<float> heights, int n)
    {
        var min = Math.Min(MinAutoSpeakers, n); var max = Math.Min(MaxAutoSpeakers, n);
        var best = min; var bestGap = float.MinValue;
        for (var k = min; k <= max; k++)
        {
            // stopping at k groups means the merge that would join k into k-1 is heights[n - k] (0-based), after the last done merge heights[n - k - 1]
            var next = heights[n - k]; var last = n - k - 1 >= 0 ? heights[n - k - 1] : 0f;
            var gap = next - last;
            if (gap > bestGap) { bestGap = gap; best = k; }
        }
        return best;
    }

    private static float[] Normalise(float[] v)
    {
        double sum = 0; foreach (var x in v) sum += x * x;
        var norm = (float)Math.Sqrt(sum);
        return norm < 1e-9f ? v : v.Select(x => x / norm).ToArray();
    }
}
