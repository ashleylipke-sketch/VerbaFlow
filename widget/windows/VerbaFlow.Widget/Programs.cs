using System.Diagnostics;
using System.Text;

namespace VerbaFlow.Widget;

/// <summary>
/// The programs a person can name by voice: the windows open now (for "switch to …") and the Start menu's programs
/// (for "open …"). Names come from the program file, its description and the window title.
/// </summary>
internal static class Programs
{
    public sealed record Window(IntPtr Handle, string Program, IReadOnlyList<string> Names);
    public sealed record Shortcut(string Path, string Name);

    /// <summary>The windows on the taskbar, the one most recently used first (the Widget's own left out).</summary>
    public static List<Window> OpenWindows()
    {
        Descriptions.Clear(); // a process id can be used again by another program
        var list = new List<Window>();
        Native.EnumWindows((h, _) =>
        {
            try
            {
                if (!Native.IsWindowVisible(h) || Native.GetWindow(h, Native.GW_OWNER) != IntPtr.Zero) return true;
                if ((Native.GetWindowLong(h, Native.GWL_EXSTYLE) & Native.WS_EX_TOOLWINDOW) != 0) return true;
                if (Native.DwmGetWindowAttribute(h, Native.DWMWA_CLOAKED, out var cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
                var title = new StringBuilder(512);
                Native.GetWindowText(h, title, title.Capacity);
                if (title.Length == 0) return true;
                Native.GetWindowThreadProcessId(h, out var pid);
                if (pid == Environment.ProcessId) return true;
                var d = Descriptions.GetOrAdd(pid, Describe);
                // Store apps (WhatsApp, new Outlook…) run inside ApplicationFrameHost: only their title names them.
                var host = d.File.Equals("applicationframehost.exe", StringComparison.OrdinalIgnoreCase);
                var names = host ? ProgramNames.Of(null, null, title.ToString()) : ProgramNames.Of(d.File, d.Description, title.ToString());
                if (names.Count > 0) list.Add(new Window(h, d.File, names));
            }
            catch (Exception) { /* a window that closed meanwhile */ }
            return true;
        }, IntPtr.Zero);
        return list;
    }

    /// <summary>Program file and description by process, for one look at the windows.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<uint, (string File, string? Description)> Descriptions = new();

    private static (string File, string? Description) Describe(uint pid)
    {
        try
        {
            using var p = Process.GetProcessById((int)pid);
            string? description = null;
            try { description = p.MainModule?.FileVersionInfo.FileDescription; } catch (Exception) { /* another user's or protected */ }
            return ((p.ProcessName + ".exe").ToLowerInvariant(), description);
        }
        catch (Exception) { return ("unknown.exe", null); }
    }

    private static List<Shortcut>? _shortcuts;
    private static DateTime _shortcutsRead;

    /// <summary>The Start menu's programs (everyone's and the person's own), read again every 10 minutes.</summary>
    public static List<Shortcut> StartMenu()
    {
        if (_shortcuts is not null && DateTime.UtcNow - _shortcutsRead < TimeSpan.FromMinutes(10)) return _shortcuts;
        var list = new List<Shortcut>();
        foreach (var folder in new[] { Environment.SpecialFolder.CommonStartMenu, Environment.SpecialFolder.StartMenu })
        {
            try
            {
                var root = Path.Combine(Environment.GetFolderPath(folder), "Programs");
                if (!Directory.Exists(root)) continue;
                foreach (var file in Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories))
                {
                    var name = Path.GetFileNameWithoutExtension(file);
                    if (name.Contains("uninstall", StringComparison.OrdinalIgnoreCase) || name.Contains("readme", StringComparison.OrdinalIgnoreCase)) continue;
                    var spoken = ProgramNames.Clean(name);
                    if (spoken.Length >= 2 && spoken.Split(' ').Length <= 5) list.Add(new Shortcut(file, spoken));
                }
            }
            catch (Exception) { /* a folder that cannot be read */ }
        }
        _shortcuts = list.GroupBy(s => s.Name).Select(g => g.First()).ToList();
        _shortcutsRead = DateTime.UtcNow;
        return _shortcuts;
    }

    /// <summary>"switch to Word": the most recently used Word window, other than the one in front. False when none is open.</summary>
    public static bool SwitchTo(string spoken)
    {
        var front = Native.GetForegroundWindow();
        var windows = OpenWindows();
        var pairs = windows.Where(w => w.Handle != front).Concat(windows.Where(w => w.Handle == front))
            .SelectMany(w => w.Names.Select(n => (n, w)));
        var match = ProgramNames.Best(spoken, pairs);
        return match is not null && Native.BringToFront(match.Handle);
    }

    /// <summary>"open Excel": starts the Start menu's program of that name. False when there is none.</summary>
    public static bool Open(string spoken)
    {
        var match = ProgramNames.Best(spoken, StartMenu().Select(s => (s.Name, s)));
        if (match is null) return false;
        try { Process.Start(new ProcessStartInfo(match.Path) { UseShellExecute = true }); return true; }
        catch (Exception) { return false; }
    }

    /// <summary>The names the recogniser listens for after "switch to" and "open".</summary>
    public static (List<string> Open, List<string> Start) Names() =>
        (OpenWindows().SelectMany(w => w.Names).Distinct().ToList(), StartMenu().Select(s => s.Name).Distinct().ToList());

    /// <summary>Presses a key named by voice in the program in front.</summary>
    public static void Press(VoiceKey key)
    {
        Native.ReleaseModifiers();
        switch (key)
        {
            case VoiceKey.NextCell or VoiceKey.Tab: Native.Press(Native.VK_TAB); break;
            case VoiceKey.PreviousCell: Native.Press(Native.VK_SHIFT, Native.VK_TAB); break;
            case VoiceKey.Down: Native.Press(Native.VK_DOWN); break;
            case VoiceKey.Up: Native.Press(Native.VK_UP); break;
            case VoiceKey.Right: Native.Press(Native.VK_RIGHT); break;
            case VoiceKey.Left: Native.Press(Native.VK_LEFT); break;
            case VoiceKey.Enter: Native.Press(Native.VK_RETURN); break;
            case VoiceKey.Escape: Native.Press(Native.VK_ESCAPE); break;
            case VoiceKey.Backspace: Native.Press(Native.VK_BACK); break;
            case VoiceKey.Delete: Native.Press(Native.VK_DELETE); break;
        }
    }

    /// <summary>"go to cell B5": Excel's Go To box (Ctrl+G), the address, Enter.</summary>
    public static async Task GoToCellAsync(string cell)
    {
        Native.ReleaseModifiers();
        Native.Press(Native.VK_CONTROL, Native.VK_G);
        await Task.Delay(400);
        await Native.TypeAsync(cell);
        await Task.Delay(100);
        Native.Press(Native.VK_RETURN);
    }
}
