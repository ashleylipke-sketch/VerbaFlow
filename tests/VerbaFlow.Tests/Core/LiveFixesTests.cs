using VerbaFlow.Core.Transcripts;

namespace VerbaFlow.Tests.Core;

public class LiveFixesTests
{
    [Theory]
    [InlineData("Dear Sir", "Dear Sir", "Dear Madam", "Dear Madam")]                       // the same words
    [InlineData("Dear Sir, thank you", "Dear Sir,", "Dear Madam,", "Dear Madam, thank you")] // more heard after it
    [InlineData("Dear Sir, thank you", "Dear Sir.", "Dear Madam.", "Dear Madam, thank you")] // its full stop became a comma
    [InlineData("Hello there", "Dear Sir", "Dear Madam", null)]                              // the words are gone
    public void A_correction_is_put_back_into_the_passage_it_was_made_on(string current, string original, string text, string? expected) =>
        Assert.Equal(expected, LiveFixes.Fit(current, new LiveFix(0, original, text)));

    [Fact]
    public void Only_the_passage_that_started_at_the_same_moment_is_changed_and_corrections_build_on_each_other()
    {
        Segment S(int at, string text) => new(Guid.NewGuid(), Guid.Empty, "en", at, at + 900, text, 1, false);
        var (segs, fixd) = LiveFixes.Apply([S(0, "one two"), S(1000, "one two")],
            [new LiveFix(1010, "one two", "one too"), new LiveFix(1000, "one too", "won too")]);
        Assert.Equal(["one two", "won too"], segs.Select(s => s.Text));
        Assert.Equal([1], fixd);
    }
}
