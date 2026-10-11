using VerbaFlow.Core.Transcripts;
using VerbaFlow.Core.Providers;
using VerbaFlow.Infrastructure.Azure;

namespace VerbaFlow.Tests.Core;

/// <summary>Cases from the owner's first real dictation (2026-10-11).</summary>
public class DictationRealTests
{
    private static Segment Seg(string text, IReadOnlyList<WordTiming>? words = null) =>
        new(Guid.NewGuid(), Guid.NewGuid(), "en", 0, 9000, text, 0.9, false, words);

    [Theory]
    [InlineData("I'm going to comma close quote Burnham told")]
    [InlineData("I'm going to, comma, close quote, Burnham told")]
    [InlineData("I'm going to, close quote Burnham told")]
    public void A_comma_before_a_closing_quote_is_kept(string spoken) =>
        Assert.Equal("I'm going to,\" Burnham told", DictationCommands.Apply(Seg(spoken)).Segments.Single().Text);

    [Fact]
    public void A_line_break_written_by_the_speech_service_starts_a_new_paragraph_and_word_timings_still_line_up()
    {
        var text = "financial rules.\nAn independent commission";
        var words = "financial rules an independent commission".Split(' ').Select((w, i) => new WordTiming(w, i * 300, i * 300 + 250)).ToList();
        var (segs, applied, heard) = DictationCommands.ApplyCounted([Seg(text, words)]);
        Assert.Equal(["financial rules.", "An independent commission"], segs.Select(s => s.Text));
        Assert.Equal([true, true], segs.Select(s => s.NewParagraph == true));
        Assert.Equal(600, segs[1].StartMs);
        Assert.Equal(1, heard["line break from the speech service"]);
    }

    [Fact]
    public void Command_counts_name_the_commands_only()
    {
        var (_, _, heard) = DictationCommands.ApplyCounted([Seg("Hello comma world full stop new paragraph Bye comma all")]);
        Assert.Equal(new Dictionary<string, int> { ["comma"] = 2, ["full stop"] = 1, ["new paragraph"] = 1 }, heard);
    }

    [Fact]
    public void A_dictation_keeps_line_breaks_from_azure_including_a_phrase_that_is_only_a_line_break()
    {
        var json = """
            {"phrases":[
            {"offsetMilliseconds":0,"durationMilliseconds":1000,"text":"First part.","confidence":0.9},
            {"offsetMilliseconds":1100,"durationMilliseconds":500,"text":"\n\n","confidence":0.9},
            {"offsetMilliseconds":1700,"durationMilliseconds":1000,"text":"Second part.","confidence":0.9}]}
            """;
        var dictation = new TranscribeOptions(["en-GB"], 8, false, null);
        var merged = AzureSpeechService.Parse(json, dictation).Segments;
        var (segs, _) = DictationCommands.Apply(merged);
        Assert.Equal(["First part.", "Second part."], segs.Select(s => s.Text));
        Assert.Equal([true, true], segs.Select(s => s.NewParagraph == true));
        // A meeting is unchanged: the line-break-only phrase is dropped.
        Assert.Single(AzureSpeechService.Parse(json, dictation with { Diarize = true }).Segments);
    }
}
