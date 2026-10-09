using VerbaFlow.Core.Providers;
using VerbaFlow.Infrastructure.Services;
using Xunit;

namespace VerbaFlow.Tests.Services;

public class WindowedGroupingTests
{
    // Fake fingerprints: each "voice" is a direction, with a little noise.
    private static float[] Voice(int who, int noise)
    {
        var v = new float[8]; v[who] = 1f; v[(who + 4) % 8] = 0.15f * (noise % 3);
        v[7 - who % 4] += 0.02f * noise;
        return v;
    }

    [Fact]
    public void Separates_four_voices_and_estimates_the_count()
    {
        var prints = new List<float[]>(); var truth = new List<int>();
        for (var i = 0; i < 80; i++) { var who = (i / 5) % 4; prints.Add(Voice(who, i)); truth.Add(who); }
        var labels = VoiceClusterer.Cluster(prints, null);
        Assert.Equal(4, labels.Distinct().Count());
        for (var i = 0; i < 80; i++) for (var j = 0; j < 80; j++) Assert.Equal(truth[i] == truth[j], labels[i] == labels[j]);
    }

    [Fact]
    public void A_given_count_is_obeyed()
    {
        var prints = Enumerable.Range(0, 40).Select(i => Voice(i % 4, i)).ToList();
        Assert.Equal(2, VoiceClusterer.Cluster(prints, 2).Distinct().Count());
        Assert.Equal(3, VoiceClusterer.Cluster(prints, 3).Distinct().Count());
    }

    [Fact]
    public void Tiny_inputs_do_not_break()
    {
        Assert.Empty(VoiceClusterer.Cluster([], null));
        Assert.Equal([0], VoiceClusterer.Cluster([[1f, 0f]], null));
        Assert.Equal(2, VoiceClusterer.Cluster([[1f, 0f], [0f, 1f]], null).Distinct().Count());
    }

    [Fact]
    public void Windows_cover_speech_and_skip_blips()
    {
        var regions = WindowedTurns.SpeechRegions([new(0, 4000, 0), new(4100, 5000, 1), new(9000, 9300, 2), new(12000, 13000, 0)]);
        Assert.Equal([(0, 5000), (9000, 9300), (12000, 13000)], regions);
        var windows = WindowedTurns.MakeWindows(regions);
        Assert.DoesNotContain(windows, w => w.Region == 1); // 300 ms is too short to fingerprint
        Assert.All(windows, w => Assert.True(w.EndMs - w.StartMs <= WindowedTurns.WindowMs));
        Assert.Equal(5000, windows.Where(w => w.Region == 0).Max(w => w.EndMs));
    }

    [Fact]
    public void Turns_follow_the_labels_and_cover_each_region()
    {
        var regions = new List<(int, int)> { (0, 6000) };
        var windows = WindowedTurns.MakeWindows(regions);
        // first half voice 0, second half voice 1, with one stray window in the middle of voice 0
        var labels = windows.Select((w, i) => i == 2 ? 1 : w.StartMs < 3000 ? 0 : 1).ToList();
        var turns = WindowedTurns.ToTurns(regions, windows, labels);
        Assert.Equal(2, turns.Count);
        Assert.Equal(0, turns[0].StartMs); Assert.Equal(6000, turns[^1].EndMs);
        Assert.Equal(turns[0].EndMs, turns[1].StartMs);
        Assert.NotEqual(turns[0].Speaker, turns[1].Speaker);
    }
}
