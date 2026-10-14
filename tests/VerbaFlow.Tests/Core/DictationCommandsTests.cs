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

    [Fact]
    public void Bullet_and_numbered_points_start_list_paragraphs_and_end_list_goes_back_to_ordinary_text()
    {
        var (segs, _, heard) = DictationCommands.ApplyCounted([
            Seg("Please bring colon bullet point your passport bullet point two photos"),
            Seg("end list The steps are colon numbered point. Sign the form. Number point pay the fee"),
            Seg("new paragraph Regards")]);
        Assert.Equal("Please bring: | Your passport | Two photos | The steps are: | Sign the form. | Pay the fee | Regards", Text(segs)); // the speech service's own full stop stays unless only spoken punctuation is kept
        Assert.Equal([null, "bullet", "bullet", null, "number", "number", null], segs.Select(x => x.List));
        Assert.All(segs.Skip(1), x => Assert.True(x.NewParagraph));
        Assert.Equal(2, heard["bullet point"]);
        Assert.Equal(["•", "•", null, "1.", "2.", null], DictationCommands.ListMarkers(["bullet", "bullet", null, "number", "number", null]).ToArray());
    }

    [Fact]
    public void A_list_point_said_at_the_end_of_a_passage_carries_over_to_the_next_passage_and_carrying_on_text_is_not_a_new_point()
    {
        var (segs, _, _) = DictationCommands.ApplyCounted([Seg("Items colon numbered point"), Seg("First thing"), Seg("still the first thing")]);
        Assert.Equal("Items: | First thing | still the first thing", Text(segs));
        Assert.Equal([null, "number", null], segs.Select(x => x.List));
        Assert.Equal([true, true, false], segs.Select(x => x.NewParagraph));
        Assert.Equal(["1.", "2."], DictationCommands.ListMarkers(["number", "number"]).ToArray());
        Assert.Equal(["1.", null, "1."], DictationCommands.ListMarkers(["number", null, "number"]).ToArray());
    }

    [Fact]
    public void Plural_points_and_endless_inside_a_list_are_taken_as_list_commands()
    {
        var (segs, _, heard) = DictationCommands.ApplyCounted([
            Seg("The steps are colon numbered point sign the form numbered points pay the fee endless regards")], spokenPunctuationOnly: true);
        Assert.Equal("The steps are: | Sign the form | Pay the fee | Regards", Text(segs));
        Assert.Equal([null, "number", "number", null], segs.Select(x => x.List));
        Assert.Equal(1, heard["numbered points"]);
        Assert.Equal(1, heard["end list (heard as endless)"]);
    }

    [Fact]
    public void Endless_outside_a_list_stays_a_word()
    {
        var (segs, _, _) = DictationCommands.ApplyCounted([Seg("The choice is endless")]);
        Assert.Equal("The choice is endless", Text(segs));
    }

    [Theory]
    [InlineData("Ashley dot Lipke @btinternet.com", "ashley.lipke@btinternet.com")]
    [InlineData("send it to Ashley dot Lipke @btinternet.com full stop", "send it to ashley.lipke@btinternet.com.")]
    [InlineData("ashley dot lipke at btinternet dot com", "ashley.lipke@btinternet.com")]
    [InlineData("j underscore smith @example dot co dot uk comma thanks", "j_smith@example.co.uk, thanks")]
    [InlineData("Lipke@BTinternet.com", "lipke@btinternet.com")]
    [InlineData("you can find it at example.com", "you can find it at example.com")]
    [InlineData("meet me at home", "meet me at home")]
    public void Spoken_email_addresses_become_one_word(string spoken, string expected)
    {
        var (segs, _, heard) = DictationCommands.ApplyCounted([Seg(spoken)], spokenPunctuationOnly: true);
        Assert.Equal(expected, Text(segs));
        if (expected.Contains('@')) Assert.True(heard.ContainsKey(DictationCommands.EmailJoined));
        Assert.Equal(segs[0].Text.Split(' ').Length, segs[0].Words!.Count);
    }

    [Fact]
    public void A_comma_or_colon_said_before_any_word_is_kept_for_the_text_already_in_the_box()
    {
        var (segs, _, _) = DictationCommands.ApplyCounted([Seg("colon new paragraph the information provided")], spokenPunctuationOnly: true);
        Assert.Equal(": | The information provided", Text(segs));
        Assert.Equal([":"], segs[0].Words!.Select(w => w.Text));
    }

    [Theory]
    [InlineData("new paragraph the information", true)]
    [InlineData("bullet point first", true)]
    [InlineData("colon new paragraph the information", false)]
    [InlineData("the information", false)]
    public void Starts_with_break_tells_whether_speech_began_with_new_paragraph(string spoken, bool expected) =>
        Assert.Equal(expected, DictationCommands.StartsWithBreak([spoken]));

    [Theory]
    [InlineData("I will see you on Tuesday full stop we can meet at ten scratch that we can meet at noon full stop",
        "I will see you on Tuesday. We can meet at noon.")]
    [InlineData("I will see you on Tuesday full stop scratch that I will see you on Thursday full stop", "I will see you on Thursday.")]
    [InlineData("the price is high comma scratch that low", "Low")]
    [InlineData("Dear George comma I hope this finds you scratch that I hope you are well", "Dear George, I hope you are well")]
    [InlineData("scratch that hello", "Hello")]
    public void Scratch_that_takes_away_what_was_said_since_the_last_sentence_end(string spoken, string expected)
    {
        var (segs, _, heard) = DictationCommands.ApplyCounted([Seg(spoken)], spokenPunctuationOnly: true);
        Assert.Equal(expected, Text(segs));
        Assert.Equal(1, heard["scratch that"]);
        Assert.Equal(segs.Sum(s => s.Text.Split(' ').Length), segs.Sum(s => s.Words!.Count));
    }

    [Fact]
    public void Scratch_that_after_a_new_paragraph_goes_back_into_the_paragraph_before()
    {
        var (segs, _, _) = DictationCommands.ApplyCounted([Seg("Dear George comma new paragraph scratch that new paragraph Hello")], spokenPunctuationOnly: true);
        Assert.Equal("Hello", Text(segs));
    }

    [Fact]
    public void Scratch_that_takes_away_saved_text_as_a_whole()
    {
        var (segs, _, _) = DictationCommands.ApplyCounted([Seg("Thanks full stop insert disclaimer one scratch that Regards")],
            [("disclaimer 1", "No advice is given. Read it carefully.")], spokenPunctuationOnly: true);
        Assert.Equal("Thanks. Regards", Text(segs));
    }

    [Theory]
    [InlineData("we meet on Tuesday at noon full stop replace Tuesday with Thursday", "we meet on Thursday at noon.")]
    [InlineData("Dear Jon comma replace Jon with John", "Dear John,")]
    [InlineData("the fee is fifty pounds full stop correct fifty pounds to sixty pounds", "the fee is sixty pounds.")]
    [InlineData("please replace the old policy with the new one", "please replace the old policy with the new one")]
    public void Replace_changes_the_last_place_the_words_were_said(string spoken, string expected)
    {
        var (segs, _, heard) = DictationCommands.ApplyCounted([Seg(spoken)], spokenPunctuationOnly: true);
        Assert.Equal(expected, Text(segs));
        Assert.Equal(expected.Contains("replace") ? 1 : 0, heard.GetValueOrDefault(DictationCommands.ReplaceNotFound));
        Assert.Equal(segs[0].Text.Split(' ').Length, segs[0].Words!.Count);
    }

    [Fact]
    public void Replace_stops_at_a_pause_so_the_dictation_carries_on()
    {
        var words = "we meet on Tuesday replace Tuesday with Thursday and then lunch".Split(' ');
        var timings = words.Select((w, i) => new WordTiming(w, i * 500 + (i >= 8 ? 1000 : 0), i * 500 + 400 + (i >= 8 ? 1000 : 0))).ToList();
        var seg = new Segment(Guid.NewGuid(), Guid.NewGuid(), "en", 0, 7000, string.Join(' ', words), 0.9, false, timings);
        var (segs, _, _) = DictationCommands.ApplyCounted([seg], spokenPunctuationOnly: true);
        Assert.Equal("we meet on Thursday and then lunch", Text(segs));
    }
}
