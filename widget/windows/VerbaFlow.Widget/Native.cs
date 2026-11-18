using System.Runtime.InteropServices;

namespace VerbaFlow.Widget;

/// <summary>The few Windows functions the Widget needs: the shortcut, the window in front, the cursor, and key presses.</summary>
internal static partial class Native
{
    public const int WM_HOTKEY = 0x0312;
    public const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8, MOD_NOREPEAT = 0x4000;
    public const ushort VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_LWIN = 0x5B, VK_RWIN = 0x5C, VK_V = 0x56, VK_D = 0x44, VK_DELETE = 0x2E, VK_INSERT = 0x2D, VK_RETURN = 0x0D;

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnregisterHotKey(IntPtr hWnd, int id);

    [LibraryImport("user32.dll")]
    public static partial IntPtr GetForegroundWindow();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetForegroundWindow(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindow(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    public static partial uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [LibraryImport("user32.dll")]
    public static partial short GetAsyncKeyState(int vKey);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct GUITHREADINFO
    {
        public int cbSize, flags;
        public IntPtr hwndActive, hwndFocus, hwndCapture, hwndMenuOwner, hwndMoveSize, hwndCaret;
        public RECT rcCaret;
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool GetGUIThreadInfo(uint threadId, ref GUITHREADINFO info);

    [DllImport("user32.dll")]
    public static extern bool ClientToScreen(IntPtr hWnd, ref POINT point);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint type; public KEYBDINPUT ki; private readonly long _pad; }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, INPUT[] inputs, int size);

    private const uint INPUT_KEYBOARD = 1, KEYEVENTF_KEYUP = 0x2, KEYEVENTF_UNICODE = 0x4;

    /// <summary>
    /// Types the text as a keyboard would: each character as itself (any language, not the keyboard layout's), Enter
    /// for a line break. Sent in small batches so slow programs and remote desktops keep up.
    /// </summary>
    public static async Task TypeAsync(string text)
    {
        var batch = new List<INPUT>();
        async Task Send()
        {
            if (batch.Count == 0) return;
            SendInput((uint)batch.Count, [.. batch], Marshal.SizeOf<INPUT>());
            batch.Clear();
            await Task.Delay(15);
        }
        foreach (var k in Insertion.Keys(text))
        {
            if (k.Enter) { batch.Add(Key(VK_RETURN, false)); batch.Add(Key(VK_RETURN, true)); }
            else
            {
                var c = k.Character!.Value;
                batch.Add(new INPUT { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wScan = c, dwFlags = KEYEVENTF_UNICODE } });
                batch.Add(new INPUT { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wScan = c, dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP } });
            }
            if (batch.Count >= 40) await Send();
        }
        await Send();
    }

    // Whether a program runs as administrator: Windows does not let an ordinary program type into one.
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000, TOKEN_QUERY = 0x8;
    private const int TokenElevation = 20;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr token, int infoClass, out int info, int length, out int returned);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    /// <summary>True when the process runs as administrator, false when not, null when Windows will not say.</summary>
    public static bool? IsElevated(uint pid)
    {
        var process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == IntPtr.Zero) return null;
        try { return TokenElevated(process); }
        finally { CloseHandle(process); }
    }

    public static bool SelfElevated => TokenElevated(GetCurrentProcess()) == true;

    private static bool? TokenElevated(IntPtr process)
    {
        if (!OpenProcessToken(process, TOKEN_QUERY, out var token)) return null;
        try { return GetTokenInformation(token, TokenElevation, out var elevated, sizeof(int), out _) ? elevated != 0 : null; }
        finally { CloseHandle(token); }
    }

    /// <summary>Presses the keys in order, then lets them go in reverse order.</summary>
    public static void Press(params ushort[] keys)
    {
        var inputs = new List<INPUT>();
        foreach (var k in keys) inputs.Add(Key(k, false));
        foreach (var k in keys.Reverse()) inputs.Add(Key(k, true));
        SendInput((uint)inputs.Count, [.. inputs], Marshal.SizeOf<INPUT>());
    }

    /// <summary>Lets go of Shift, Ctrl, Alt and Windows if they are held, so they do not change a paste.</summary>
    public static void ReleaseModifiers()
    {
        var up = new[] { VK_SHIFT, VK_CONTROL, VK_MENU, VK_LWIN, VK_RWIN }.Where(k => (GetAsyncKeyState(k) & 0x8000) != 0).Select(k => Key(k, true)).ToArray();
        if (up.Length > 0) SendInput((uint)up.Length, up, Marshal.SizeOf<INPUT>());
    }

    private static INPUT Key(ushort vk, bool up) => new() { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = vk, dwFlags = up ? KEYEVENTF_KEYUP : 0 } };
}
