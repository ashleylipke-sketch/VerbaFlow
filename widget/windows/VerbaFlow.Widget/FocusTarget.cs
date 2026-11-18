using System.Diagnostics;
using System.Windows.Automation;
using System.Windows.Automation.Text;

namespace VerbaFlow.Widget;

/// <summary>
/// Where the words go: the window in front when the shortcut was pressed, its program, where the cursor is, and (where
/// the program tells Windows) the selected text, the character before the cursor and whether it is a password box.
/// </summary>
internal sealed record FocusTarget(IntPtr Window, string Program, Rectangle Anchor, bool IsPassword, string SelectedText, string? TextBefore)
{
    /// <summary>
    /// Why the words cannot be put in here and are copied instead (a program run as administrator, or a place that
    /// cannot be typed in), or null.
    /// </summary>
    public string? CopyReason { get; init; }

    public const string AdminReason = "This program is running as administrator, so Windows does not let the Widget type into it. " +
        "The words will be copied: paste them with Ctrl+V.";
    public const string ReadOnlyReason = "The place you clicked cannot be typed in. The words will be copied: click where you want them and paste with Ctrl+V.";

    /// <summary>The window in front now, or null when it is the Widget's own or none.</summary>
    public static FocusTarget? Capture()
    {
        var window = Native.GetForegroundWindow();
        if (window == IntPtr.Zero) return null;
        var thread = Native.GetWindowThreadProcessId(window, out var pid);
        if (pid == Environment.ProcessId) return null;
        var program = ProgramOf(pid);

        var anchor = Rectangle.Empty;
        var info = new Native.GUITHREADINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.GUITHREADINFO>() };
        if (Native.GetGUIThreadInfo(thread, ref info) && info.hwndCaret != IntPtr.Zero && info.rcCaret.Bottom > info.rcCaret.Top)
        {
            var p = new Native.POINT { X = info.rcCaret.Left, Y = info.rcCaret.Top };
            Native.ClientToScreen(info.hwndCaret, ref p);
            anchor = new Rectangle(p.X, p.Y, Math.Max(2, info.rcCaret.Right - info.rcCaret.Left), info.rcCaret.Bottom - info.rcCaret.Top);
        }

        // UI Automation can be slow or stuck in a busy program, so it gets a moment at most, away from the Widget's window thread.
        var inspect = Task.Run(Inspect);
        var (password, selected, before, box, readOnly) = inspect.Wait(1500) ? inspect.Result : (false, "", null, Rectangle.Empty, false);
        if (anchor.IsEmpty) anchor = !box.IsEmpty ? box : Native.GetWindowRect(window, out var r)
            ? new Rectangle((r.Left + r.Right) / 2 - 200, r.Bottom - 260, 1, 1) : new Rectangle(Cursor.Position, new Size(1, 1));
        var admin = !Native.SelfElevated && Native.IsElevated(pid) == true;
        return new FocusTarget(window, program, anchor, password, selected, before)
        {
            CopyReason = admin ? AdminReason : readOnly ? ReadOnlyReason : null,
        };
    }

    private static (bool Password, string Selected, string? Before, Rectangle Box, bool ReadOnly) Inspect()
    {
        try
        {
            var el = AutomationElement.FocusedElement;
            if (el is null) return (false, "", null, Rectangle.Empty, false);
            var password = el.Current.IsPassword;
            var b = el.Current.BoundingRectangle;
            var box = b.IsEmpty || double.IsInfinity(b.Width) ? Rectangle.Empty : new Rectangle((int)b.X, (int)b.Y, (int)b.Width, Math.Min((int)b.Height, 40));
            // Only a box that says so itself counts as read-only: many programs say nothing, and those are tried.
            var readOnly = !password && el.TryGetCurrentPattern(ValuePattern.Pattern, out var v) && v is ValuePattern value && value.Current.IsReadOnly;
            if (password || !el.TryGetCurrentPattern(TextPattern.Pattern, out var o) || o is not TextPattern text) return (password, "", null, box, readOnly);
            var ranges = text.GetSelection();
            if (ranges.Length == 0) return (false, "", null, box, readOnly);
            var selected = ranges[0].GetText(5000);
            var before = ranges[0].Clone();
            before.MoveEndpointByRange(TextPatternRangeEndpoint.End, before, TextPatternRangeEndpoint.Start);
            before.MoveEndpointByUnit(TextPatternRangeEndpoint.Start, TextUnit.Character, -1);
            return (false, selected, before.GetText(2), box, readOnly);
        }
        catch (Exception) { return (false, "", null, Rectangle.Empty, false); } // the program does not say
    }

    private static string ProgramOf(uint pid)
    {
        try { using var p = Process.GetProcessById((int)pid); return (p.ProcessName + ".exe").ToLowerInvariant(); }
        catch (Exception) { return "unknown.exe"; }
    }
}
