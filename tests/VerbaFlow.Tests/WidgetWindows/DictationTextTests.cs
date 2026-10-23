using VerbaFlow.Widget;

namespace VerbaFlow.Tests.WidgetWindows;

public class DictationTextTests
{
    private static LivePara P(string text, bool? newParagraph = null, string? list = null) => new(text, newParagraph, list);

    [Fact]
    public void Passages_become_paragraphs_with_list_markers_as_on_the_item_page()
    {
        var segs = new[]
        {
            P("Dear John,"), P("thank you.", false), P("Please bring:", true),
            P("eggs", true, "number"), P("milk", true, "number"), P("Then\nsee you.", true),
            P("tea", true, "bullet"), P("", true),
        };
        Assert.Equal(["Dear John, thank you.", "Please bring:", "1. eggs", "2. milk", "Then", "see you.", "• tea"], DictationText.Paragraphs(segs));
        Assert.Equal("Dear John, thank you.\n\nPlease bring:", DictationText.Heard([segs[0], segs[1], segs[2]], false));
        Assert.Equal("\n\nHello", DictationText.Heard([P("Hello")], true)); // "new paragraph" said first
        Assert.Equal("", DictationText.Heard([], true));
    }

    [Fact]
    public void Speaking_more_joins_on_with_a_space_unless_it_starts_with_punctuation_or_a_break()
    {
        Assert.Equal("Hello there", DictationText.Join("Hello", "there"));
        Assert.Equal("Hello, there", DictationText.Join("Hello", ", there"));
        Assert.Equal("Hello\n\nthere", DictationText.Join("Hello ", "\n\nthere"));
        Assert.Equal("Hello", DictationText.Join("Hello  ", ""));
        Assert.Equal("there", DictationText.Join("  ", "there"));

        Assert.Equal(" words", DictationText.Spaced("Dear", "words"));
        Assert.Equal("words", DictationText.Spaced("Dear ", "words"));
        Assert.Equal(", words", DictationText.Spaced("Dear", ", words"));
        Assert.Equal("words", DictationText.Spaced(null, "words"));
        Assert.Equal("\r\nwords", DictationText.Spaced("Dear", "\r\nwords"));
        Assert.Equal(3, DictationText.Words(" one\ntwo  three "));
    }

    [Fact]
    public void Pasted_text_has_windows_line_ends_and_word_gets_no_empty_line_between_paragraphs()
    {
        const string text = "First line\nsame paragraph\n\nSecond\n\n\nThird\n";
        Assert.Equal("First line\r\nsame paragraph\r\n\r\nSecond\r\n\r\nThird", DictationText.ForPasting(text, true));
        Assert.Equal("First line\r\nsame paragraph\r\nSecond\r\nThird", DictationText.ForPasting(text, false));
        Assert.Equal("\r\n\r\nNew one", DictationText.ForPasting("\n\nNew one", true));

        Assert.False(DictationText.EmptyLineFor(ParagraphGap.Automatic, "WINWORD.EXE"));
        Assert.True(DictationText.EmptyLineFor(ParagraphGap.Automatic, "outlook.exe"));
        Assert.True(DictationText.EmptyLineFor(ParagraphGap.EmptyLine, "winword.exe"));
        Assert.False(DictationText.EmptyLineFor(ParagraphGap.NewLineOnly, "outlook.exe"));
    }

    [Fact]
    public void Settings_are_cleaned_and_a_damaged_settings_file_gives_the_defaults()
    {
        Assert.Equal("http://localhost:5044", WidgetSettings.CleanServer(" localhost:5044/ "));
        Assert.Equal("https://verbaflow.example.com", WidgetSettings.CleanServer("verbaflow.example.com"));
        Assert.Equal("https://x.example.com/vf", WidgetSettings.CleanServer("https://x.example.com/vf/?a=1"));
        Assert.Null(WidgetSettings.CleanServer("ftp://x"));
        Assert.Null(WidgetSettings.CleanServer(""));

        var dir = Directory.CreateTempSubdirectory("vf-widget-").FullName;
        try
        {
            var path = Path.Combine(dir, "sub", "settings.json");
            Assert.Equal(new WidgetSettings(), WidgetSettings.Load(path));
            new WidgetSettings { Server = "https://vf.example.com/", Languages = " en-ZA , af-ZA ", TestUser = " Bob ", ParagraphGap = ParagraphGap.NewLineOnly }.Save(path);
            var s = WidgetSettings.Load(path);
            Assert.Equal(("https://vf.example.com", "en-ZA,af-ZA", "bob", ParagraphGap.NewLineOnly, true), (s.Server, s.Languages, s.TestUser, s.ParagraphGap, s.LeaveBrowsersToExtension));
            Assert.Contains("\"newLineOnly\"", File.ReadAllText(path), StringComparison.OrdinalIgnoreCase);
            File.WriteAllText(path, "{ not json");
            Assert.Equal(new WidgetSettings(), WidgetSettings.Load(path));
        }
        finally { Directory.Delete(dir, true); }
    }
}
