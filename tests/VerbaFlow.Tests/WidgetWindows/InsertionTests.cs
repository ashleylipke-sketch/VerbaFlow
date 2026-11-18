using VerbaFlow.Widget;

namespace VerbaFlow.Tests.WidgetWindows;

public class InsertionTests
{
    [Fact]
    public void Programs_get_the_usual_way_unless_known_or_chosen()
    {
        Assert.Equal(InsertMethod.Paste, Insertion.DefaultFor("notepad.exe"));
        Assert.Equal(InsertMethod.ShiftInsert, Insertion.DefaultFor("PuTTY.exe"));
        Assert.Equal(InsertMethod.Type, Insertion.DefaultFor("mstsc.exe"));

        var s = new WidgetSettings().WithMethod("Notepad.exe", InsertMethod.Type);
        Assert.Equal(InsertMethod.Type, s.MethodFor("notepad.exe"));
        Assert.Equal(InsertMethod.Type, s.MethodFor("NOTEPAD.EXE"));
        s = s.WithMethod("notepad.exe", InsertMethod.Paste); // the usual way again: forgotten
        Assert.Empty(s.InsertMethods);
        s = s.WithMethod("mstsc.exe", InsertMethod.Paste);    // a known program's way can be changed back to pasting
        Assert.Equal(InsertMethod.Paste, s.MethodFor("mstsc.exe"));
    }

    [Fact]
    public void Choices_and_the_shortcut_are_kept_in_the_settings_file()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "settings.json");
            new WidgetSettings { Shortcut = "ctrl+shift+f9" }.WithMethod("putty.exe", InsertMethod.Copy).Save(path);
            Assert.Contains("\"copy\"", File.ReadAllText(path), StringComparison.OrdinalIgnoreCase);
            var back = WidgetSettings.Load(path);
            Assert.Equal("Ctrl+Shift+F9", back.Shortcut);
            Assert.Equal(InsertMethod.Copy, back.MethodFor("putty.exe"));

            File.WriteAllText(path, """{"shortcut":"Shift+D","insertMethods":{"x.exe":"type"}}""");
            back = WidgetSettings.Load(path);
            Assert.Equal(Shortcut.Default, back.Keys); // Shift+D alone would type a capital D
            Assert.Equal(InsertMethod.Type, back.MethodFor("x.exe"));
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public void Shortcuts_need_ctrl_alt_or_windows_and_are_never_altgr()
    {
        Assert.Equal("Alt+Shift+D", Shortcut.Default.ToString());
        Assert.Equal(new Shortcut(true, false, true, false, "F9"), Shortcut.Parse("shift + ctrl + f9"));
        Assert.Equal("Ctrl+Win+Space", Shortcut.Parse("Win+Ctrl+space")!.ToString());
        Assert.Null(Shortcut.Parse("Shift+D"));
        Assert.Null(Shortcut.Parse("Ctrl+Alt+E")); // AltGr+E is € on many keyboards
        Assert.NotNull(Shortcut.Parse("Ctrl+Alt+Shift+E"));
        Assert.Null(Shortcut.Parse("Alt+Shift"));
        Assert.Null(Shortcut.Parse("Alt+D+E"));
        Assert.Null(Shortcut.Parse("Alt+Home"));
        Assert.Equal((ushort)'D', Shortcut.Default.VirtualKey);
        Assert.Equal((ushort)0x78, Shortcut.Parse("Alt+F9")!.VirtualKey);
        Assert.Equal((ushort)0x20, Shortcut.Parse("Alt+Space")!.VirtualKey);
        Assert.All(Shortcut.Holds, h => Assert.NotNull(Shortcut.From(h, "D")));
    }

    [Fact]
    public void Typing_turns_line_breaks_into_enter_and_keeps_every_other_character()
    {
        var keys = Insertion.Keys("é,\r\nb\n\tç").ToList();
        Assert.Equal([new('é', false), new(',', false), new(null, true), new('b', false), new(null, true), new('\t', false), new('ç', false)], keys);
    }
}
