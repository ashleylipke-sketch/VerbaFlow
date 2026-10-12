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

    [Theory]
    [InlineData("As per our meeting on Thursday 22 November 2023. Comma. Please find a summary.", "As per our meeting on Thursday 22 November 2023, please find a summary.")]
    [InlineData("Thank you. Comma, I will call you.", "Thank you, I will call you.")]
    [InlineData("We spoke to the client. Comma. BBC staff were there.", "We spoke to the client, BBC staff were there.")]
    [InlineData("I met Sarah today. Colon. Sarah agreed.", "I met Sarah today: Sarah agreed.")]
    [InlineData("Is that right? Comma. It is.", "Is that right, it is.")]
    [InlineData("As per our meeting on Thursday 22 November 2023 comma. Please find a summary.", "As per our meeting on Thursday 22 November 2023, please find a summary.")]
    [InlineData("Thank you comma John will call.", "Thank you, John will call.")]
    public void A_spoken_comma_that_replaces_a_full_stop_also_takes_away_the_capital_it_gave(string spoken, string expected) =>
        Assert.Equal(expected, Text(DictationCommands.Apply(Seg(spoken)).Segments));

    [Fact]
    public void Names_in_the_custom_vocabulary_keep_their_capital_after_a_comma()
    {
        var (segs, _, _) = DictationCommands.ApplyCounted([Seg("Thank you. Comma. Lee-Anne will call.")], names: ["Lee-Anne"]);
        Assert.Equal("Thank you, Lee-Anne will call.", Text(segs));
    }

    [Fact]
    public void History_counts_capitals_taken_away_and_kept_after_a_spoken_comma()
    {
        var (_, _, heard) = DictationCommands.ApplyCounted([Seg("Dated 2023 comma. Please reply. Thank you comma John will call.")]);
        Assert.Equal(1, heard[DictationCommands.LoweredName]);
        Assert.Equal(1, heard[DictationCommands.KeptName]);
    }

    private static string SpokenOnly(params string[] passages) =>
        Text(DictationCommands.ApplyCounted(passages.Select(p => Seg(p)).ToList(), spokenPunctuationOnly: true).Segments);

    [Theory]
    [InlineData("Please contact me if you need anything further. Regards.", "Please contact me if you need anything further regards")]
    [InlineData("I will call you, tomorrow full stop", "I will call you tomorrow.")]
    [InlineData("Is that right? Question mark", "Is that right?")]
    [InlineData("Is that right question mark", "Is that right?")]
    [InlineData("Thank you. Comma. Please reply full stop", "Thank you, please reply.")]
    [InlineData("Ask Mr. Smith about e.g. the U.K. office full stop", "Ask Mr. Smith about e.g. the U.K. office.")]
    [InlineData("It costs 2.5 million. And more full stop", "It costs 2.5 million and more.")]
    [InlineData("We met. I agreed full stop", "We met I agreed.")]
    [InlineData("He said open quote hello. Close quote full stop", "He said \"hello\".")]
    public void Spoken_punctuation_only_takes_away_what_the_speech_service_added(string spoken, string expected) =>
        Assert.Equal(expected, SpokenOnly(spoken));

    [Fact]
    public void Spoken_punctuation_only_works_across_passages_and_keeps_paragraph_starts()
    {
        Assert.Equal("Please contact me if you need anything | further.", SpokenOnly("Please contact me if you need anything.", "Further full stop"));
        Assert.Equal("Thank you | Yours sincerely", SpokenOnly("Thank you. New paragraph.", "Yours sincerely."));
        // Sarah is capitalised mid-sentence elsewhere, so it is a name and keeps its capital.
        Assert.Equal("We met Sarah | Sarah agreed", SpokenOnly("We met Sarah.", "Sarah agreed."));
    }

    [Fact]
    public void Without_spoken_punctuation_only_the_speech_service_punctuation_stays() =>
        Assert.Equal("Please contact me if you need anything further. Regards.",
            Text(DictationCommands.ApplyCounted([Seg("Please contact me if you need anything further. Regards.")]).Segments));

    [Fact]
    public void History_counts_punctuation_and_capitals_taken_away()
    {
        var (_, _, heard) = DictationCommands.ApplyCounted([Seg("Anything further. Regards.")], spokenPunctuationOnly: true);
        Assert.Equal(2, heard[DictationCommands.RemovedPunctuation]);
        Assert.Equal(1, heard[DictationCommands.RemovedCapital]);
    }
}
