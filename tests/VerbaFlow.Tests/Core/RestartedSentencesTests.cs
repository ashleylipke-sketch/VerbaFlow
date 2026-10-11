using VerbaFlow.Core.Transcripts;

namespace VerbaFlow.Tests.Core;

public class RestartedSentencesTests
{
    private static Segment Seg(string text)
    {
        var words = text.Split(' ');
        return new Segment(Guid.NewGuid(), Guid.NewGuid(), "en", 0, words.Length * 500, text, 0.9, false,
            words.Select((w, i) => new WordTiming(w, i * 500, i * 500 + 400)).ToList());
    }

    [Fact]
    public void A_restarted_sentence_is_taken_out_and_the_rest_keeps_its_timings()
    {
        var r = RestartedSentences.Remove(Seg("He spoke. \"I will quite get it. I will quite clearly put out there everything.\" Then left."))!.Value;
        Assert.Equal("He spoke. \"I will quite clearly put out there everything.\" Then left.", r.Segment.Text);
        Assert.Equal(1, r.Removed);
        Assert.Equal(3500, r.Segment.Words![2].StartMs); // the second "I" (8th word spoken)
    }

    [Theory]
    [InlineData("I will go. I will go.")]                              // the restart must be longer
    [InlineData("We agree. We agree completely with the plan.")]       // fewer than three words
    [InlineData("Thank you for your letter. We will reply soon.")]     // different start
    public void Ordinary_text_is_left_alone(string text) => Assert.Null(RestartedSentences.Remove(Seg(text)));
}
