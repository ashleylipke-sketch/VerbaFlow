using VerbaFlow.Core.Transcripts;

namespace VerbaFlow.Tests.Core;

public class SpokenInsertTests
{
    private static readonly List<(string, string)> Saved =
    [
        ("legal 1,1", "This letter is confidential."),
        ("legal 1.2", "Nothing in this letter is legal advice."),
        ("disclaimer 2.0", "First paragraph of the disclaimer.\nSecond paragraph of the disclaimer."),
        ("sign off", "Yours sincerely"),
    ];

    private static Segment Seg(string text)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return new Segment(Guid.NewGuid(), Guid.NewGuid(), "en", 0, words.Length * 500, text, 0.9, false,
            words.Select((w, i) => new WordTiming(w, i * 500, i * 500 + 400)).ToList());
    }

    private static IReadOnlyList<Segment> Run(string spoken) => DictationCommands.ApplyCounted([Seg(spoken)], Saved).Segments;
    private static string Text(IReadOnlyList<Segment> s) => string.Join(" | ", s.Select(x => x.Text));

    [Theory]
    [InlineData("legal 1,1", "legal 1.1")]
    [InlineData("Legal 1.1.", "legal 1.1")]
    [InlineData("legal 1, 1", "legal 1.1")]
    [InlineData("legal one point one", "legal 1.1")]
    [InlineData("legal one comma one", "legal 1.1")]
    [InlineData("disclaimer two point oh", "disclaimer 2.0")]
    [InlineData("clause twenty one", "clause 21")]
    [InlineData("Sign-off", "sign off")]
    public void Codes_typed_or_spoken_compare_the_same(string said, string key) => Assert.Equal(key, SpokenCodes.Key(said));

    [Theory]
    [InlineData("Please note. Insert legal 1.1. Kind regards", "Please note. This letter is confidential. Kind regards")]
    [InlineData("Please note insert legal one point one kind regards", "Please note This letter is confidential. kind regards")]
    [InlineData("Please note insert legal 1, 1 comma kind regards", "Please note This letter is confidential, kind regards")]
    [InlineData("Insert legal 1.2.", "Nothing in this letter is legal advice.")]
    [InlineData("insert sign off", "Yours sincerely")]
    public void Insert_and_a_code_put_the_saved_text_in_its_place(string spoken, string expected) =>
        Assert.Equal(expected, Text(Run(spoken)));

    [Fact]
    public void Saved_text_with_several_paragraphs_starts_a_paragraph_for_each_and_is_marked()
    {
        var segs = Run("As agreed insert disclaimer 2.0 thank you");
        Assert.Equal("As agreed First paragraph of the disclaimer. | Second paragraph of the disclaimer. thank you", Text(segs));
        Assert.Equal([true, true], segs.Select(s => s.NewParagraph == true));
        Assert.All(segs, s => Assert.Equal("disclaimer 2.0", s.Inserted));
    }

    [Fact]
    public void Inserted_words_are_timed_across_the_spoken_command()
    {
        // "insert" is word 2 (starts 1000 ms), "1.1." is word 4 (ends 2400 ms)
        var s = Run("Please note insert legal 1.1. Kind regards").Single();
        var inserted = s.Words!.Skip(2).Take(4).ToList();
        Assert.Equal(["This", "letter", "is", "confidential."], inserted.Select(w => w.Text));
        Assert.Equal(1000, inserted[0].StartMs);
        Assert.Equal(2400, inserted[^1].EndMs);
        Assert.Equal(2500, s.Words![6].StartMs); // "Kind" keeps its own time
    }

    [Fact]
    public void An_unknown_code_leaves_the_words_as_spoken_and_is_reported()
    {
        var (segs, applied, heard) = DictationCommands.ApplyCounted([Seg("insert legal 9.9 please")], Saved);
        Assert.Equal("insert legal 9.9 please", Text(segs));
        Assert.Equal(0, applied);
        Assert.Equal(1, heard[DictationCommands.NoMatch]);
        Assert.Null(segs[0].Inserted);
    }

    [Fact]
    public void Insert_as_an_ordinary_word_is_left_alone()
    {
        var (segs, _, heard) = DictationCommands.ApplyCounted([Seg("insert the key full stop")], Saved);
        Assert.Equal("insert the key.", Text(segs));
        Assert.DoesNotContain(heard, h => h.Key.StartsWith("insert"));
    }

    [Fact]
    public void Commands_inside_saved_text_are_not_applied()
    {
        var saved = new List<(string, string)> { ("note 1", "Put a comma here and a full stop there") };
        Assert.Equal("Put a comma here and a full stop there", Text(DictationCommands.ApplyCounted([Seg("insert note 1")], saved).Segments));
    }

    [Fact]
    public void Restarted_sentence_check_never_touches_saved_text()
    {
        var saved = new List<(string, string)> { ("terms 1", "The client shall pay. The client shall pay the fee within thirty days.") };
        var seg = DictationCommands.ApplyCounted([Seg("insert terms 1")], saved).Segments.Single();
        Assert.Null(RestartedSentences.Remove(seg));
    }
}
