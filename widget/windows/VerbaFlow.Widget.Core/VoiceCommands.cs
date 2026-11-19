using System.Text;
using System.Text.RegularExpressions;

namespace VerbaFlow.Widget;

/// <summary>What a spoken command asks the Widget to do.</summary>
public enum VoiceAction
{
    /// <summary>Start dictating where the cursor is.</summary>
    Dictate,
    /// <summary>Bring an open program to the front.</summary>
    SwitchTo,
    /// <summary>Start a program, or, when no program has that name, open a saved document of that name.</summary>
    Open,
    /// <summary>Open a saved document ("open the document budget").</summary>
    OpenFile,
    /// <summary>Paste one or more saved documents into the program in front, as attachments ("attach budget and invoice").</summary>
    AttachFiles,
    /// <summary>Press a key (moving between cells, Enter, Tab, Escape...).</summary>
    Key,
    /// <summary>Go to a cell of a spreadsheet, such as B5.</summary>
    GoToCell,
    Accept,
    SpeakMore,
    FixGrammar,
    UndoFix,
    Cancel,
}

/// <summary>A key the person can press by voice.</summary>
public enum VoiceKey { NextCell, PreviousCell, Down, Up, Right, Left, Enter, Tab, Escape, Backspace, Delete, NewDocument }

/// <summary>One spoken command: what to do, and the program, key or cell it names.</summary>
public sealed record VoiceCommand(VoiceAction Action, string? Name = null, VoiceKey? Key = null, string? Cell = null);

/// <summary>
/// Hands-free use: the commands said to the Widget, as the computer's own speech recogniser writes them. Commands start
/// with the wake word "VerbaFlow" ("VerbaFlow, switch to Outlook"); while the words are being checked, "accept",
/// "speak more", "fix grammar", "undo fix" and "cancel" work without it. While dictating, "stop dictating" at the end
/// finishes. English only.
/// </summary>
public static class VoiceCommands
{
    /// <summary>How the wake word is written by recognisers (after lower case and punctuation are taken away).</summary>
    public static readonly string[] WakeForms = ["verbaflow", "verba flow", "verb a flow", "verbal flow", "verba flo", "hey verbaflow", "hey verba flow", "ok verbaflow", "ok verba flow"];

    /// <summary>The wake word as the Windows recogniser is taught it.</summary>
    public const string WakePhrase = "verba flow";

    public static readonly string[] DictatePhrases = ["dictate", "start dictating", "start dictation", "take dictation", "dictation"];
    public static readonly string[] SwitchWords = ["switch to", "go to", "show me", "show"];
    public static readonly string[] OpenWords = ["open", "start", "launch"];
    /// <summary>Said between "open" and a name to mean a saved document, not a program.</summary>
    public static readonly string[] FileWords = ["the document", "the file", "the spreadsheet", "the presentation", "document", "file", "spreadsheet", "presentation", "my"];
    public static readonly string[] AttachWords = ["attach", "add attachment", "attach the file", "attach the document", "attach file", "attach document", "attach files", "attach documents"];

    public static readonly IReadOnlyDictionary<string, VoiceKey> KeyPhrases = new Dictionary<string, VoiceKey>
    {
        ["next cell"] = VoiceKey.NextCell, ["previous cell"] = VoiceKey.PreviousCell, ["last cell"] = VoiceKey.PreviousCell,
        ["cell below"] = VoiceKey.Down, ["next row"] = VoiceKey.Down, ["down"] = VoiceKey.Down, ["move down"] = VoiceKey.Down,
        ["cell above"] = VoiceKey.Up, ["up"] = VoiceKey.Up, ["move up"] = VoiceKey.Up,
        ["cell to the right"] = VoiceKey.Right, ["cell right"] = VoiceKey.Right, ["right"] = VoiceKey.Right, ["move right"] = VoiceKey.Right,
        ["cell to the left"] = VoiceKey.Left, ["cell left"] = VoiceKey.Left, ["left"] = VoiceKey.Left, ["move left"] = VoiceKey.Left,
        ["press enter"] = VoiceKey.Enter, ["press return"] = VoiceKey.Enter, ["press tab"] = VoiceKey.Tab,
        ["press escape"] = VoiceKey.Escape, ["press backspace"] = VoiceKey.Backspace, ["press delete"] = VoiceKey.Delete,
        // A new blank document (Ctrl+N), e.g. from Word's start screen. Before "open X", so it is not taken as a program.
        ["new document"] = VoiceKey.NewDocument, ["new blank document"] = VoiceKey.NewDocument, ["blank document"] = VoiceKey.NewDocument,
        ["open a new document"] = VoiceKey.NewDocument, ["open new document"] = VoiceKey.NewDocument,
        ["open a blank document"] = VoiceKey.NewDocument, ["open blank document"] = VoiceKey.NewDocument,
        ["new file"] = VoiceKey.NewDocument, ["new email"] = VoiceKey.NewDocument, ["new spreadsheet"] = VoiceKey.NewDocument,
    };

    public static readonly IReadOnlyDictionary<string, VoiceAction> CheckPhrases = new Dictionary<string, VoiceAction>
    {
        ["accept"] = VoiceAction.Accept, ["accept it"] = VoiceAction.Accept, ["insert it"] = VoiceAction.Accept, ["put it in"] = VoiceAction.Accept,
        ["speak more"] = VoiceAction.SpeakMore, ["add more"] = VoiceAction.SpeakMore, ["carry on"] = VoiceAction.SpeakMore, ["keep going"] = VoiceAction.SpeakMore,
        ["fix grammar"] = VoiceAction.FixGrammar, ["fix the grammar"] = VoiceAction.FixGrammar,
        ["undo fix"] = VoiceAction.UndoFix, ["undo the fix"] = VoiceAction.UndoFix,
        ["cancel"] = VoiceAction.Cancel, ["throw it away"] = VoiceAction.Cancel, ["discard"] = VoiceAction.Cancel,
    };

    /// <summary>Said at the end of a dictation to finish it.</summary>
    public static readonly string[] StopPhrases = ["stop dictating", "stop dictation", "finish dictating", "finish dictation", "end dictation", "stop listening"];

    public const string ListeningHint = "Say \"stop dictating\" when you have finished";
    public const string CheckHint = "Say \"accept\", \"speak more\", \"fix grammar\" or \"cancel\"";

    /// <summary>
    /// What was heard, as a command, or null. Outside the check step a command needs the wake word; while checking,
    /// the check commands work with or without it.
    /// </summary>
    public static VoiceCommand? Parse(string heard, bool checking)
    {
        var t = Plain(heard);
        var woke = false;
        foreach (var w in WakeForms.OrderByDescending(w => w.Length))
            if (t == w || t.StartsWith(w + " ")) { t = t[w.Length..].Trim(); woke = true; break; }
        if (checking && CheckPhrases.TryGetValue(t, out var c)) return new VoiceCommand(c);
        if (!woke || t.Length == 0) return null;
        if (checking) return null; // only the check commands while the words are being checked
        if (DictatePhrases.Contains(t)) return new VoiceCommand(VoiceAction.Dictate);
        if (KeyPhrases.TryGetValue(t, out var k)) return new VoiceCommand(VoiceAction.Key, Key: k);
        var cell = Regex.Match(t, @"^go to (?:cell )?(.+)$");
        if (cell.Success && CellOf(cell.Groups[1].Value) is { } address) return new VoiceCommand(VoiceAction.GoToCell, Cell: address);
        foreach (var s in SwitchWords.OrderByDescending(s => s.Length))
            if (t.StartsWith(s + " ") && t[(s.Length + 1)..].Trim() is { Length: > 0 } name) return new VoiceCommand(VoiceAction.SwitchTo, name);
        foreach (var a in AttachWords.OrderByDescending(a => a.Length))
            if (t.StartsWith(a + " ") && t[(a.Length + 1)..].Trim() is { Length: > 0 } files) return new VoiceCommand(VoiceAction.AttachFiles, files);
        foreach (var o in OpenWords)
        {
            if (!t.StartsWith(o + " ") || t[(o.Length + 1)..].Trim() is not { Length: > 0 } name) continue;
            foreach (var f in FileWords.OrderByDescending(f => f.Length))
                if (name.StartsWith(f + " ") && name[(f.Length + 1)..].Trim() is { Length: > 0 } file) return new VoiceCommand(VoiceAction.OpenFile, file);
            return new VoiceCommand(VoiceAction.Open, name);
        }
        return null;
    }

    /// <summary>
    /// The text without "stop dictating" (and its punctuation) at the end, or null when it does not end with it.
    /// </summary>
    public static string? WithoutStop(string text)
    {
        var trimmed = text.TrimEnd();
        foreach (var p in StopPhrases)
        {
            var m = Regex.Match(trimmed, @"\b" + string.Join(@"[\s,.]+", p.Split(' ')) + @"[\s,.;:!?]*$", RegexOptions.IgnoreCase);
            if (m.Success) return trimmed[..m.Index].TrimEnd().TrimEnd(',', ';', ':').TrimEnd();
        }
        return null;
    }

    /// <summary>A cell address from what was heard ("b5", "b 5", "bee five", "aa twelve"), upper case, or null.</summary>
    public static string? CellOf(string heard)
    {
        var parts = Plain(heard).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (parts.Count == 0) return null;
        var letters = new StringBuilder();
        var i = 0;
        // "b5" as one word
        var joined = Regex.Match(parts[0], @"^([a-z]{1,3})(\d{1,7})$");
        if (joined.Success && parts.Count == 1) return joined.Groups[1].Value.ToUpperInvariant() + joined.Groups[2].Value;
        while (i < parts.Count && letters.Length < 3 && LetterOf(parts[i]) is { } l) { letters.Append(l); i++; }
        if (letters.Length == 0 || i == parts.Count) return null;
        var number = NumberOf(parts.Skip(i).ToList());
        return number is > 0 and <= 1_048_576 ? letters.ToString() + number : null;
    }

    private static readonly Dictionary<string, char> LetterNames = new()
    {
        ["a"] = 'A', ["ay"] = 'A', ["b"] = 'B', ["bee"] = 'B', ["be"] = 'B', ["c"] = 'C', ["see"] = 'C', ["sea"] = 'C', ["d"] = 'D', ["dee"] = 'D',
        ["e"] = 'E', ["f"] = 'F', ["ef"] = 'F', ["g"] = 'G', ["gee"] = 'G', ["h"] = 'H', ["aitch"] = 'H', ["i"] = 'I', ["j"] = 'J', ["jay"] = 'J',
        ["k"] = 'K', ["kay"] = 'K', ["l"] = 'L', ["el"] = 'L', ["m"] = 'M', ["em"] = 'M', ["n"] = 'N', ["en"] = 'N', ["o"] = 'O', ["oh"] = 'O',
        ["p"] = 'P', ["pee"] = 'P', ["q"] = 'Q', ["queue"] = 'Q', ["cue"] = 'Q', ["r"] = 'R', ["are"] = 'R', ["s"] = 'S', ["ess"] = 'S',
        ["t"] = 'T', ["tee"] = 'T', ["tea"] = 'T', ["u"] = 'U', ["you"] = 'U', ["v"] = 'V', ["vee"] = 'V', ["w"] = 'W', ["x"] = 'X', ["ex"] = 'X',
        ["y"] = 'Y', ["why"] = 'Y', ["z"] = 'Z', ["zed"] = 'Z', ["zee"] = 'Z',
    };

    private static char? LetterOf(string word) => LetterNames.TryGetValue(word, out var c) ? c : null;

    private static readonly string[] Units = ["zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten", "eleven", "twelve",
        "thirteen", "fourteen", "fifteen", "sixteen", "seventeen", "eighteen", "nineteen"];
    private static readonly string[] Tens = ["", "", "twenty", "thirty", "forty", "fifty", "sixty", "seventy", "eighty", "ninety"];

    /// <summary>A row number from digits or words up to 9,999 ("twelve", "one hundred and five", "1 2").</summary>
    private static int? NumberOf(List<string> words)
    {
        words = words.Where(w => w != "and").ToList();
        if (words.Count == 0) return null;
        if (words.All(w => w.All(char.IsDigit))) return int.TryParse(string.Concat(words), out var d) ? d : null;
        int total = 0, current = 0;
        foreach (var w in words)
        {
            var u = Array.IndexOf(Units, w);
            var t = Array.IndexOf(Tens, w);
            if (u >= 0) current += u;
            else if (t >= 2) current += t * 10;
            else if (w == "hundred") current = Math.Max(current, 1) * 100;
            else if (w == "thousand") { total += Math.Max(current, 1) * 1000; current = 0; }
            else if (w.All(char.IsDigit) && int.TryParse(w, out var n)) current += n;
            else return null;
        }
        return total + current;
    }

    /// <summary>Lower case, letters, digits and single spaces only.</summary>
    public static string Plain(string text)
    {
        var b = new StringBuilder();
        foreach (var ch in text.ToLowerInvariant())
            b.Append(char.IsLetterOrDigit(ch) ? ch : ' ');
        return Regex.Replace(b.ToString(), @"\s+", " ").Trim();
    }
}

/// <summary>The names a program is called by when switching to it or opening it by voice.</summary>
public static class ProgramNames
{
    /// <summary>Program files whose name is not what people call them.</summary>
    private static readonly Dictionary<string, string> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["winword"] = "word", ["excel"] = "excel", ["powerpnt"] = "powerpoint", ["outlook"] = "outlook", ["olk"] = "outlook",
        ["msedge"] = "edge", ["chrome"] = "chrome", ["firefox"] = "firefox", ["ms-teams"] = "teams", ["teams"] = "teams",
        ["whatsapp"] = "whatsapp", ["whatsapp.root"] = "whatsapp", ["notepad"] = "notepad", ["explorer"] = "file explorer", ["onenote"] = "onenote",
        ["acrobat"] = "acrobat", ["acrord32"] = "acrobat", ["code"] = "visual studio code", ["slack"] = "slack", ["zoom"] = "zoom",
        ["thunderbird"] = "thunderbird", ["calc"] = "calculator", ["mspub"] = "publisher", ["msaccess"] = "access", ["visio"] = "visio",
    };

    /// <summary>Words left out of a name: "Microsoft Word" is "word", "Outlook (classic)" is "outlook".</summary>
    private static readonly string[] Dropped = ["microsoft", "google", "mozilla", "adobe", "the"];

    /// <summary>
    /// The spoken names of a program, from its file name (with or without .exe), its description and a window title
    /// (the part after the last " - ", where most programs put their name).
    /// </summary>
    public static IReadOnlyList<string> Of(string? file, string? description = null, string? title = null)
    {
        var names = new List<string>();
        var stem = Path.GetFileNameWithoutExtension(file ?? "");
        if (stem.Length > 0) names.Add(Known.TryGetValue(stem, out var k) ? k : Clean(stem));
        if (!string.IsNullOrWhiteSpace(description)) names.Add(Clean(description));
        if (!string.IsNullOrWhiteSpace(title)) names.Add(Clean(title.Split(" - ").Last()));
        return names.Where(n => n.Length >= 2 && n.Split(' ').Length <= 4 && n.Any(char.IsLetter)).Distinct().ToList();
    }

    /// <summary>A name as it would be said: no brackets, no company words, lower case.</summary>
    public static string Clean(string name)
    {
        var n = Regex.Replace(name, @"\([^)]*\)|\[[^\]]*\]", " ");
        n = VoiceCommands.Plain(n);
        var words = n.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => !Dropped.Contains(w)).ToList();
        return words.Count == 0 ? n : string.Join(" ", words);
    }

    /// <summary>
    /// The item whose name fits what was said best: the same name, else a name that starts with it or holds all its
    /// words. Earlier items win a tie (so the window most recently used comes first). Null when nothing fits.
    /// </summary>
    public static T? Best<T>(string spoken, IEnumerable<(string Name, T Item)> candidates) where T : class
    {
        var s = Clean(spoken);
        if (s.Length == 0) return null;
        var list = candidates.ToList();
        var words = s.Split(' ');
        return list.FirstOrDefault(c => c.Name == s).Item
            ?? list.FirstOrDefault(c => c.Name.Replace(" ", "") == s.Replace(" ", "")).Item
            ?? list.FirstOrDefault(c => c.Name.StartsWith(s + " ")).Item
            ?? list.FirstOrDefault(c => words.All(w => c.Name.Split(' ').Contains(w))).Item;
    }
}

/// <summary>
/// The names saved documents are called by when opening or attaching them by voice: the file name without its type,
/// as it would be said ("Budget_2025-final.xlsx" is "budget 2025 final").
/// </summary>
public static class DocumentNames
{
    /// <summary>The kinds of file the Widget offers by voice: documents, spreadsheets, presentations, PDFs, pictures.</summary>
    public static readonly HashSet<string> Kinds = new(StringComparer.OrdinalIgnoreCase)
    {
        ".doc", ".docx", ".docm", ".dotx", ".rtf", ".odt", ".txt", ".md", ".pdf",
        ".xls", ".xlsx", ".xlsm", ".xlsb", ".ods", ".csv",
        ".ppt", ".pptx", ".pptm", ".ppsx", ".odp",
        ".pages", ".numbers", ".key",
        ".jpg", ".jpeg", ".png", ".gif", ".heic", ".bmp", ".tif", ".tiff",
        ".msg", ".eml", ".vsdx", ".pub", ".one", ".zip",
    };

    /// <summary>Words people add after a name that are not part of it ("the budget spreadsheet").</summary>
    private static readonly string[] TypeWords = ["word document", "excel file", "excel spreadsheet", "powerpoint presentation", "powerpoint",
        "document", "doc", "file", "spreadsheet", "workbook", "presentation", "slides", "slideshow", "pdf", "picture", "photo", "email"];

    /// <summary>Whether a file is one the Widget offers (by its type; hidden Office lock files "~$…" left out).</summary>
    public static bool Offered(string path)
    {
        var name = Path.GetFileName(path);
        return name.Length > 0 && !name.StartsWith("~$") && !name.StartsWith('.') && Kinds.Contains(Path.GetExtension(name));
    }

    /// <summary>The spoken name of a file, or "" when it has none that could be said (only digits, or very long).</summary>
    public static string Of(string path)
    {
        // Windows paths are read the same on any computer (the tests run on Linux too).
        var stem = Path.GetFileNameWithoutExtension(path[(path.LastIndexOfAny(['\\', '/']) + 1)..]);
        // "QuarterlyReport" is said "quarterly report".
        stem = Regex.Replace(stem, @"(?<=[a-z])(?=[A-Z])", " ");
        var n = ProgramNames.Clean(stem);
        var words = n.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length is >= 1 and <= 8 && n.Any(char.IsLetter) && n.Length >= 2 ? n : "";
    }

    /// <summary>The ways a spoken name may be meant: as said, then without a kind word at the end ("budget spreadsheet" is "budget").</summary>
    public static IEnumerable<string> Forms(string spoken)
    {
        var s = ProgramNames.Clean(spoken);
        if (s.StartsWith("my ")) s = s[3..];
        yield return s;
        foreach (var t in TypeWords)
            if (s.EndsWith(" " + t) && s[..^(t.Length + 1)].Trim() is { Length: > 0 } shorter) { yield return shorter; break; }
    }

    /// <summary>The document that fits what was said best (newest first wins a tie), or null.</summary>
    public static T? Best<T>(string spoken, IReadOnlyList<(string Name, T Item)> documents) where T : class
    {
        foreach (var form in Forms(spoken))
            if (ProgramNames.Best(form, documents) is { } found) return found;
        return null;
    }

    /// <summary>
    /// The documents named in "budget and invoice and the minutes": the whole as one name first (a file may be called
    /// "terms and conditions"), else each part. Missing holds the parts no document fits.
    /// </summary>
    public static (List<T> Found, List<string> Missing) Pick<T>(string spoken, IReadOnlyList<(string Name, T Item)> documents) where T : class
    {
        if (Best(spoken, documents) is { } whole) return ([whole], []);
        var found = new List<T>();
        var missing = new List<string>();
        foreach (var part in Regex.Split(VoiceCommands.Plain(spoken), @"\s+(?:and|plus|also)\s+").Where(p => p.Length > 0))
        {
            if (Best(part, documents) is { } d) { if (!found.Contains(d)) found.Add(d); }
            else missing.Add(part);
        }
        return (found, missing);
    }
}
