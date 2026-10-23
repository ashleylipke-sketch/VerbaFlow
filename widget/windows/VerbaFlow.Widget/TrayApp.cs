using System.Diagnostics;

namespace VerbaFlow.Widget;

/// <summary>
/// The Widget for Windows: an icon near the clock and the Alt+Shift+D shortcut. It runs until Quit is chosen from the
/// icon's menu, and can start with Windows (in the settings).
/// </summary>
internal sealed class TrayApp : ApplicationContext
{
    public static readonly Icon AppIcon = new(typeof(TrayApp).Assembly.GetManifestResourceStream("widget.ico")!);
    private const int HotkeyId = 1;

    private readonly NotifyIcon _tray;
    private readonly HotkeyWindow _hotkeys;
    private readonly WidgetController _controller;
    private readonly WindowsSignIn _signIn;
    private WidgetSettings _settings;
    private VerbaFlowClient _client;
    private HttpClient _http;
    private SettingsForm? _settingsForm;
    private bool _registered;

    public TrayApp(bool firstRun)
    {
        _settings = WidgetSettings.Load(WidgetSettings.DefaultPath);
        _signIn = new WindowsSignIn(() => _settings);
        (_http, _client) = NewClient();
        _controller = new WidgetController(() => _client, () => _settings, Notify, OpenSettings);

        var menu = new ContextMenuStrip();
        menu.Items.Add("Settings and sign-in…", null, (_, _) => OpenSettings());
        menu.Items.Add("Open VerbaFlow", null, (_, _) => Open(_settings.Server));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit the Widget", null, (_, _) => ExitThread());
        _tray = new NotifyIcon { Icon = AppIcon, Text = "VerbaFlow Widget (Alt+Shift+D)", ContextMenuStrip = menu, Visible = true };
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) OpenSettings(); };

        _hotkeys = new HotkeyWindow(OnHotkey);
        Register();
        if (firstRun) OpenSettings();
        else if (_registered) Notify("VerbaFlow Widget is running. Click where you want the words, then press Alt+Shift+D.");
    }

    private (HttpClient, VerbaFlowClient) NewClient()
    {
        var http = new HttpClient { BaseAddress = new Uri(_settings.Server + "/"), Timeout = TimeSpan.FromSeconds(60) };
        return (http, new VerbaFlowClient(http, _signIn));
    }

    private void Register()
    {
        _registered = Native.RegisterHotKey(_hotkeys.Handle, HotkeyId, Native.MOD_ALT | Native.MOD_SHIFT | Native.MOD_NOREPEAT, Native.VK_D);
        if (!_registered) Notify("Alt+Shift+D is taken by another program, so the Widget cannot use it. Close that program, then quit and start the Widget again.");
    }

    private async void OnHotkey()
    {
        try { await HotkeyAsync(); }
        catch (Exception) { Notify("Something went wrong in the Widget. Try again."); }
    }

    private async Task HotkeyAsync()
    {
        var target = FocusTarget.Capture();
        if (target is null) return;
        if (_settings.LeaveBrowsersToExtension && target.Program is "chrome.exe" or "msedge.exe")
        {
            // Websites are the browser Widget's: it knows the website, and an administrator's blocks apply to websites.
            Native.UnregisterHotKey(_hotkeys.Handle, HotkeyId);
            Native.Press(Native.VK_MENU, Native.VK_SHIFT, Native.VK_D);
            await Task.Delay(200);
            Register();
            return;
        }
        await _controller.ShortcutAsync(target);
    }

    public void Notify(string text) => _tray.ShowBalloonTip(6000, "VerbaFlow Widget", text, ToolTipIcon.None);

    private void OpenSettings()
    {
        if (_settingsForm is { IsDisposed: false }) { _settingsForm.Activate(); return; }
        _settingsForm = new SettingsForm(_settings, () => _client, _signIn, Saved);
        _settingsForm.Show();
        _settingsForm.Activate();
    }

    /// <summary>New settings: a new address means a new connection to VerbaFlow.</summary>
    private void Saved(WidgetSettings s)
    {
        var serverChanged = s.Server != _settings.Server;
        _settings = s;
        s.Save(WidgetSettings.DefaultPath);
        if (serverChanged) { var old = _http; (_http, _client) = NewClient(); old.Dispose(); }
    }

    public static void Open(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch (Exception) { /* no browser */ }
    }

    protected override void ExitThreadCore()
    {
        Native.UnregisterHotKey(_hotkeys.Handle, HotkeyId);
        _controller.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        _hotkeys.DestroyHandle();
        _http.Dispose();
        base.ExitThreadCore();
    }

    /// <summary>A hidden window that receives the shortcut.</summary>
    private sealed class HotkeyWindow : NativeWindow
    {
        private readonly Action _pressed;
        public HotkeyWindow(Action pressed) { _pressed = pressed; CreateHandle(new CreateParams()); }
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_HOTKEY) _pressed();
            base.WndProc(ref m);
        }
    }
}
