namespace VerbaFlow.Widget;

/// <summary>How the accepted words are put into a program.</summary>
public enum InsertMethod
{
    /// <summary>Paste with Ctrl+V: the usual way, understood by nearly every program.</summary>
    Paste,
    /// <summary>Paste with Shift+Insert: older programs and terminals such as PuTTY.</summary>
    ShiftInsert,
    /// <summary>Type the letters one by one, as a keyboard would: works where pasting does not (remote desktops, virtual machines).</summary>
    Type,
    /// <summary>Only copy the words; the person pastes them where they want.</summary>
    Copy,
}

/// <summary>Which way each program gets the words: the person's own choice, else a sensible one for known programs.</summary>
public static class Insertion
{
    /// <summary>Programs whose own paste is not Ctrl+V.</summary>
    private static readonly HashSet<string> ShiftInsertPrograms = new(StringComparer.OrdinalIgnoreCase)
    {
        "putty.exe", "kitty.exe", "mobaxterm.exe", "mintty.exe",
    };

    /// <summary>
    /// Remote desktops and virtual machines: a paste would use this computer's clipboard, which the other computer may
    /// not see, so the letters are typed instead.
    /// </summary>
    private static readonly HashSet<string> TypePrograms = new(StringComparer.OrdinalIgnoreCase)
    {
        "mstsc.exe", "msrdc.exe", "msrdcw.exe", "vmconnect.exe", "wfica32.exe", "cdviewer.exe", "vmware.exe", "vmplayer.exe",
        "virtualboxvm.exe", "vncviewer.exe", "tvnviewer.exe", "anydesk.exe", "teamviewer.exe",
    };

    /// <summary>The way used when the person has not chosen one for this program.</summary>
    public static InsertMethod DefaultFor(string program) =>
        ShiftInsertPrograms.Contains(program) ? InsertMethod.ShiftInsert
        : TypePrograms.Contains(program) ? InsertMethod.Type
        : InsertMethod.Paste;

    public static string Describe(InsertMethod m) => m switch
    {
        InsertMethod.ShiftInsert => "Paste with Shift+Insert",
        InsertMethod.Type => "Type the letters",
        InsertMethod.Copy => "Only copy (I paste myself)",
        _ => "Paste (Ctrl+V)",
    };

    /// <summary>
    /// What typing the text means as key presses: each character, and Enter for a line break. Tabs stay characters.
    /// </summary>
    public static IEnumerable<TypedKey> Keys(string text)
    {
        var t = text.Replace("\r\n", "\n").Replace('\r', '\n');
        foreach (var c in t)
            yield return c == '\n' ? new TypedKey(null, true) : new TypedKey(c, false);
    }
}

/// <summary>One key press when typing: a character, or Enter.</summary>
public readonly record struct TypedKey(char? Character, bool Enter);

/// <summary>
/// The keyboard shortcut that starts and stops the Widget, written as "Alt+Shift+D". It needs Ctrl, Alt or Windows, so
/// it never types a letter by itself, and never Ctrl+Alt alone (that is AltGr on many keyboards, used for letters such
/// as ą, é or €).
/// </summary>
public sealed record Shortcut(bool Ctrl, bool Alt, bool Shift, bool Win, string Key)
{
    public static readonly Shortcut Default = new(false, true, true, false, "D");

    /// <summary>The choices of keys to hold down offered in the settings.</summary>
    public static readonly string[] Holds = ["Alt+Shift", "Ctrl+Shift", "Ctrl+Alt+Shift", "Win+Alt", "Win+Shift", "Ctrl+Win"];

    /// <summary>The keys offered: letters, digits, F1 to F12 and Space.</summary>
    public static readonly string[] KeyChoices =
        [.. Enumerable.Range('A', 26).Select(c => ((char)c).ToString()), .. Enumerable.Range(0, 10).Select(d => d.ToString()),
         .. Enumerable.Range(1, 12).Select(f => $"F{f}"), "Space"];

    public string Hold => string.Join("+", new[] { (Ctrl, "Ctrl"), (Win, "Win"), (Alt, "Alt"), (Shift, "Shift") }.Where(x => x.Item1).Select(x => x.Item2)
        .OrderBy(n => Array.IndexOf(["Ctrl", "Win", "Alt", "Shift"], n)));

    public override string ToString() => Hold + "+" + Key;

    /// <summary>The Windows key code of the key.</summary>
    public ushort VirtualKey => Key switch
    {
        "Space" => 0x20,
        { Length: 1 } k => (ushort)k[0],
        var k => (ushort)(0x70 + int.Parse(k[1..]) - 1),
    };

    /// <summary>"Alt+Shift+D" as typed or stored, or null when it is not an allowed shortcut.</summary>
    public static Shortcut? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        bool ctrl = false, alt = false, shift = false, win = false;
        string? key = null;
        foreach (var raw in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var p = raw.ToUpperInvariant();
            switch (p)
            {
                case "CTRL" or "CONTROL": ctrl = true; break;
                case "ALT" or "OPTION": alt = true; break;
                case "SHIFT": shift = true; break;
                case "WIN" or "WINDOWS" or "CMD" or "COMMAND": win = true; break;
                default:
                    if (key is not null) return null;
                    key = KeyChoices.FirstOrDefault(k => k.Equals(p, StringComparison.OrdinalIgnoreCase));
                    if (key is null) return null;
                    break;
            }
        }
        if (key is null || !(ctrl || alt || win)) return null;
        if (ctrl && alt && !shift && !win) return null; // AltGr
        return new Shortcut(ctrl, alt, shift, win, key);
    }

    /// <summary>A shortcut from a choice of keys to hold and a key, as the settings offer them.</summary>
    public static Shortcut? From(string hold, string key) => Parse(hold + "+" + key);
}
