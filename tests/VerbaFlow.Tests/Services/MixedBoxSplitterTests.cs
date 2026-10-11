using VerbaFlow.Core.Providers;
using VerbaFlow.Core.Transcripts;
using VerbaFlow.Infrastructure.Services;

namespace VerbaFlow.Tests.Services;

public class MixedBoxSplitterTests
{
    private static readonly Speaker Mp = new(Guid.NewGuid(), "Speaker 1"), Chair = new(Guid.NewGuid(), "Speaker 2");

    private static Segment Seg(Speaker s, string text, params (string, int, int)[] words) =>
        new(Guid.NewGuid(), s.Id, "en", words[0].Item2, words[^1].Item3, text, 0.9, false, words.Select(w => new WordTiming(w.Item1, w.Item2, w.Item3)).ToList());

    // The MP speaks 0-6 s, the chair cuts in with "Honourable member." at 6-7.2 s inside the MP's box, then the MP goes on;
    // later the chair has a box of their own, so the local voice for 6-7.2 s belongs to the chair.
    private static TranscriptionResult Parliament() => new("azure", [Mp, Chair],
    [
        Seg(Mp, "She's going to fall. Honourable member. The minister would have fought.",
            ("she's", 0, 1500), ("going", 1500, 3000), ("to", 3000, 4500), ("fall", 4500, 6000),
            ("honourable", 6000, 6700), ("member", 6700, 7200),
            ("the", 7300, 8000), ("minister", 8000, 9000), ("would", 9000, 9500), ("have", 9500, 10000), ("fought", 10000, 11000)),
        Seg(Chair, "Order, order.", ("order", 12000, 13000), ("order", 13000, 14000)),
    ]);

    private static readonly SpeakerTurn[] Voices = [new(0, 6000, 0), new(6000, 7200, 1), new(7200, 11000, 0), new(12000, 14000, 1)];

    [Fact]
    public void A_cut_in_inside_a_box_becomes_its_own_box_for_the_right_person_with_the_punctuation_kept()
    {
        var (r, split) = MixedBoxSplitter.Split(Parliament(), Voices);
        Assert.Equal(1, split);
        Assert.Equal(["She's going to fall.", "Honourable member.", "The minister would have fought.", "Order, order."], r.Segments.Select(s => s.Text));
        Assert.Equal([Mp.Id, Chair.Id, Mp.Id, Chair.Id], r.Segments.Select(s => s.SpeakerId));
        Assert.Equal(2, r.Speakers.Count); // speakers are never renamed or added
        Assert.Equal("honourable", r.Segments[1].Words![0].Text);
    }

    [Fact]
    public void A_short_blip_of_another_voice_does_not_split_a_box()
    {
        SpeakerTurn[] blip = [new(0, 6000, 0), new(6000, 6500, 1), new(6500, 11000, 0), new(12000, 14000, 1)];
        var (r, split) = MixedBoxSplitter.Split(Parliament(), blip);
        Assert.Equal(0, split);
        Assert.Equal(2, r.Segments.Count);
    }

    [Fact]
    public void A_box_whose_words_do_not_line_up_with_its_text_is_left_alone()
    {
        var odd = Parliament() with { Segments = [Parliament().Segments[0] with { Text = "She's going to fall, honourable member, the minister would have fought it." }, Parliament().Segments[1]] };
        var (_, split) = MixedBoxSplitter.Split(odd, Voices);
        Assert.Equal(0, split);
    }

    [Fact]
    public void No_voices_from_the_local_model_changes_nothing()
    {
        var input = Parliament();
        var (r, split) = MixedBoxSplitter.Split(input, []);
        Assert.Same(input, r);
        Assert.Equal(0, split);
    }
}
