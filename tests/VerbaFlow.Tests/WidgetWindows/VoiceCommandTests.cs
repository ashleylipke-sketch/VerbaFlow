using VerbaFlow.Widget;

namespace VerbaFlow.Tests.WidgetWindows;

public class VoiceCommandTests
{
    [Fact]
    public void Commands_need_the_wake_word_except_while_checking()
    {
        Assert.Equal(new VoiceCommand(VoiceAction.Dictate), VoiceCommands.Parse("VerbaFlow, dictate.", false));
        Assert.Equal(new VoiceCommand(VoiceAction.Dictate), VoiceCommands.Parse("verba flow start dictating", false));
        Assert.Null(VoiceCommands.Parse("dictate", false));
        Assert.Null(VoiceCommands.Parse("I said accept it", true));
        Assert.Equal(new VoiceCommand(VoiceAction.Accept), VoiceCommands.Parse("Accept.", true));
        Assert.Equal(new VoiceCommand(VoiceAction.Cancel), VoiceCommands.Parse("Verba flow, cancel", true));
        Assert.Null(VoiceCommands.Parse("accept", false)); // nothing to accept outside the check step
        Assert.Null(VoiceCommands.Parse("VerbaFlow open Word", true)); // no switching while words wait to be checked
    }

    [Fact]
    public void Programs_keys_and_cells_are_read()
    {
        Assert.Equal(new VoiceCommand(VoiceAction.SwitchTo, "outlook"), VoiceCommands.Parse("VerbaFlow, switch to Outlook.", false));
        Assert.Equal(new VoiceCommand(VoiceAction.SwitchTo, "file explorer"), VoiceCommands.Parse("verbaflow go to file explorer", false));
        Assert.Equal(new VoiceCommand(VoiceAction.Open, "excel"), VoiceCommands.Parse("VerbaFlow open Excel", false));
        Assert.Equal(new VoiceCommand(VoiceAction.Key, Key: VoiceKey.NextCell), VoiceCommands.Parse("VerbaFlow, next cell", false));
        Assert.Equal(new VoiceCommand(VoiceAction.Key, Key: VoiceKey.Down), VoiceCommands.Parse("VerbaFlow down", false));
        Assert.Equal(new VoiceCommand(VoiceAction.GoToCell, Cell: "B5"), VoiceCommands.Parse("VerbaFlow, go to cell B5", false));
        Assert.Equal(new VoiceCommand(VoiceAction.GoToCell, Cell: "AA12"), VoiceCommands.Parse("verba flow go to a a twelve", false));
        Assert.Equal(new VoiceCommand(VoiceAction.GoToCell, Cell: "C105"), VoiceCommands.Parse("verba flow go to cell see one hundred and five", false));
        Assert.Equal(new VoiceCommand(VoiceAction.SwitchTo, "word"), VoiceCommands.Parse("verba flow go to word", false)); // not a cell
        Assert.Equal(new VoiceCommand(VoiceAction.Key, Key: VoiceKey.NewDocument), VoiceCommands.Parse("VerbaFlow, open a blank document.", false));
        Assert.Equal(new VoiceCommand(VoiceAction.Key, Key: VoiceKey.NewDocument), VoiceCommands.Parse("verba flow new document", false));
        Assert.Equal(new VoiceCommand(VoiceAction.Open, "word"), VoiceCommands.Parse("verba flow open word", false));
    }

    [Fact]
    public void Stop_dictating_at_the_end_finishes()
    {
        Assert.Equal("Dear Ann, thanks.", VoiceCommands.WithoutStop("Dear Ann, thanks. Stop dictating."));
        Assert.Equal("Dear Ann", VoiceCommands.WithoutStop("Dear Ann, stop dictating"));
        Assert.Equal("", VoiceCommands.WithoutStop("Stop dictating."));
        Assert.Null(VoiceCommands.WithoutStop("Please stop dictating to me and write."));
    }

    [Fact]
    public void Programs_are_found_by_what_people_call_them()
    {
        Assert.Equal(["word"], ProgramNames.Of("WINWORD.EXE", "Microsoft Word", "Letter.docx - Word"));
        Assert.Contains("outlook", ProgramNames.Of("olk.exe", "Outlook (new)"));
        Assert.Equal("teams", ProgramNames.Clean("Microsoft Teams (work or school)"));
        var windows = new[] { ("word", "w1"), ("excel", "x1"), ("visual studio code", "vs") };
        Assert.Equal("x1", ProgramNames.Best("Excel", windows.Select(w => (w.Item1, w.Item2))));
        Assert.Equal("vs", ProgramNames.Best("visual studio", windows.Select(w => (w.Item1, w.Item2))));
        Assert.Null(ProgramNames.Best("powerpoint", windows.Select(w => (w.Item1, w.Item2))));
    }

    [Fact]
    public void Saved_documents_are_opened_and_attached_by_name()
    {
        Assert.Equal(new VoiceCommand(VoiceAction.OpenFile, "budget 2025"), VoiceCommands.Parse("VerbaFlow, open the document budget 2025", false));
        Assert.Equal(new VoiceCommand(VoiceAction.OpenFile, "budget"), VoiceCommands.Parse("verba flow open file budget", false));
        Assert.Equal(new VoiceCommand(VoiceAction.Open, "budget spreadsheet"), VoiceCommands.Parse("verba flow open budget spreadsheet", false));
        Assert.Equal(new VoiceCommand(VoiceAction.AttachFiles, "the budget and invoice"), VoiceCommands.Parse("VerbaFlow, attach the budget and invoice.", false));
        Assert.Equal(new VoiceCommand(VoiceAction.AttachFiles, "minutes"), VoiceCommands.Parse("verba flow attach the file minutes", false));

        Assert.Equal("budget 2025 final", DocumentNames.Of(@"C:\Users\a\Documents\Budget_2025-final.xlsx"));
        Assert.Equal("quarterly report", DocumentNames.Of("QuarterlyReport.pptx"));
        Assert.Equal("", DocumentNames.Of("20251104.pdf"));
        Assert.True(DocumentNames.Offered("Letter.docx"));
        Assert.False(DocumentNames.Offered("~$Letter.docx"));
        Assert.False(DocumentNames.Offered("setup.exe"));

        var docs = new[] { ("budget 2025 final", "b"), ("invoice march", "i"), ("terms and conditions", "t"), ("budget draft", "old") }
            .Select(d => (d.Item1, d.Item2)).ToList();
        Assert.Equal("b", DocumentNames.Best("budget spreadsheet", docs)); // the kind word is left off; newest first
        Assert.Equal("t", DocumentNames.Best("terms and conditions", docs));
        var (found, missing) = DocumentNames.Pick("the budget and invoice and the minutes", docs);
        Assert.Equal(["b", "i"], found);
        Assert.Equal(["the minutes"], missing);
        Assert.Equal(["t"], DocumentNames.Pick("terms and conditions", docs).Found);
    }

    [Fact]
    public void Excel_gets_no_empty_lines_between_paragraphs()
    {
        Assert.False(DictationText.EmptyLineFor(ParagraphGap.Automatic, "EXCEL.EXE"));
        Assert.True(DictationText.EmptyLineFor(ParagraphGap.Automatic, "outlook.exe"));
    }
}
