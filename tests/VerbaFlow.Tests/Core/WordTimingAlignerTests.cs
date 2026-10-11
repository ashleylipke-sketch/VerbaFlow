using VerbaFlow.Core.Transcripts;

namespace VerbaFlow.Tests.Core;

public class WordTimingAlignerTests
{
    private static List<WordTiming> Spoken(string words) =>
        words.Split(' ').Select((w, i) => new WordTiming(w, 1000 + i * 500, 1000 + i * 500 + 400)).ToList();

    [Fact]
    public void Matching_words_keep_their_time_and_others_fill_the_gap()
    {
        // Shown "2009" for spoken "two thousand nine"; "comma" is a spoken word with no shown word.
        var spoken = Spoken("between two thousand nine and comma twenty eighteen");
        var t = WordTimingAligner.Align("Between 2009 and comma, 2018.", spoken, 1000, 5000);
        Assert.Equal(["Between", "2009", "and", "comma,", "2018."], t.Select(w => w.Text));
        Assert.Equal(1000, t[0].StartMs);
        Assert.Equal(3000, t[2].StartMs); // "and" is the 5th spoken word
        Assert.Equal(3500, t[3].StartMs); // "comma"
        Assert.InRange(t[1].StartMs, 1400, 3000);
        Assert.Equal(3900, t[4].StartMs); // fills from "comma"'s end to the passage end
        Assert.True(t.Zip(t.Skip(1)).All(p => p.First.StartMs <= p.Second.StartMs));
    }

    [Fact]
    public void A_stumble_left_out_of_the_shown_text_is_skipped()
    {
        var t = WordTimingAligner.Align("I will clearly", Spoken("i will quite i will clearly"), 1000, 4000);
        Assert.Equal([2500, 3000, 3500], t.Select(w => w.StartMs));
    }
}
