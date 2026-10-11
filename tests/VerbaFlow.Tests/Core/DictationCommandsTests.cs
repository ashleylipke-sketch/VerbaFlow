using VerbaFlow.Core.Transcripts;

namespace VerbaFlow.Tests.Core;

public class DictationCommandsTests
{
    private static Segment Seg(string text, bool timed = true)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return new Segment(Guid.NewGuid(), Guid.NewGuid(), "en", 0, words.Length * 500, text, 0.9, false,
            timed ? words.Select((w, i) => new WordTiming(w, i * 500, i * 500 + 400)).ToList() : null);
    }

    private static string Text(IReadOnlyList<Segment> s) => string.Join(" | ", s.Select(x => x.Text));

    [Theory]
    [InlineData("Dear John comma thank you for your letter full stop", "Dear John, thank you for your letter.")]
    [InlineData("Dear John, comma, thank you for your letter. Full stop.", "Dear John, thank you for your letter.")]
    [InlineData("is that right question mark yes it is", "is that right? Yes it is")]
    [InlineData("see open bracket clause 4 close bracket below", "see (clause 4) below")]
    [InlineData("he said open quote no close quote", "he said \"no\"")]
    [InlineData("the following colon one two", "the following: one two")]
    [InlineData("Ek is nie seker nie komma maar dit is goed vraagteken", "Ek is nie seker nie, maar dit is goed?")]
    [InlineData("No commands here at all.", "No commands here at all.")]
    public void Spoken_punctuation_replaces_the_command_words(string spoken, string expected) =>
        Assert.Equal(expected, Text(DictationCommands.Apply(Seg(spoken)).Segments));

    [Fact]
    public void New_paragraph_starts_a_new_passage_and_words_keep_their_timings()
    {
        var (segs, applied) = DictationCommands.Apply(Seg("Dear John comma new paragraph. Thank you for your letter full stop"));
        Assert.Equal(3, applied);
        Assert.Equal("Dear John, | Thank you for your letter.", Text(segs));
        Assert.Equal(0, segs[0].StartMs);
        Assert.Equal(2500, segs[1].StartMs); // "Thank" is the sixth word
        Assert.Equal(segs[0].EndMs, segs[1].StartMs);
        Assert.Equal(["Dear", "John,"], segs[0].Words!.Select(w => w.Text));
        Assert.Equal(500, segs[0].Words![1].StartMs);
    }

    [Fact]
    public void Without_word_timings_paragraph_times_are_estimated_and_a_lone_command_disappears()
    {
        var (segs, _) = DictationCommands.Apply(Seg("one two new line three four", timed: false));
        Assert.Equal("one two | Three four", Text(segs));
        Assert.Null(segs[0].Words);
        Assert.True(segs[1].StartMs > 0);
        Assert.Empty(DictationCommands.Apply(Seg("New paragraph.")).Segments);
    }

    [Fact]
    public void Words_that_merely_contain_a_command_are_left_alone() =>
        Assert.Equal("The commander stopped fully", Text(DictationCommands.Apply(Seg("The commander stopped fully")).Segments));

    [Fact]
    public void Passages_join_into_one_paragraph_until_a_new_paragraph_is_asked_for()
    {
        var (segs, _) = DictationCommands.Apply([Seg("Dear John comma"), Seg("thank you for your letter"), Seg("new paragraph"),
            Seg("I agree full stop new paragraph"), Seg("Yours sincerely"), Seg("new line Ann")]);
        Assert.Equal([true, false, true, true, true], segs.Select(x => x.NewParagraph == true).ToArray());
        var paragraphs = DictationCommands.Paragraphs(segs, x => x).Select(p => string.Join(" ", p.Select(x => x.Text))).ToList();
        Assert.Equal(["Dear John, thank you for your letter", "I agree.", "Yours sincerely", "Ann"], paragraphs);
    }

    [Fact]
    public void Passages_saved_before_paragraphs_existed_each_stand_alone() =>
        Assert.Equal(2, DictationCommands.Paragraphs([Seg("one"), Seg("two")], x => x).Count);
}
