using System.Diagnostics;

namespace VerbaFlow.Widget;

/// <summary>
/// The Widget for Windows: an icon near the clock and a keyboard shortcut (Alt+Shift+D unless changed). It runs until Quit is chosen from the
/// icon's menu, and can start with Windows (in the settings).
/// </summary>
internal sealed class TrayApp : ApplicationContext
{
    public static readonly Icon AppIcon = LoadIcon();

    private static Icon LoadIcon()
    {
        try { return new Icon(typeof(TrayApp).Assembly.GetManifestResourceStream("widget.ico")!, SystemInformation.SmallIconSize); }
        catch (Exception e) { Program.Log(e); return SystemIcons.Application; }
    }
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
    private HandsFree? _handsFree;
    private readonly ToolStripMenuItem _handsFreeItem = new("Hands-free (voice commands)");

    public TrayApp(bool firstRun)
    {
        _settings = WidgetSettings.Load(WidgetSettings.DefaultPath);
        _signIn = new WindowsSignIn(() => _settings);
        (_http, _client) = NewClient();
        _signIn.Use(() => _client);
        _controller = new WidgetController(() => _client, () => _settings, Saved, Notify, OpenSettings);
        _controller.PhaseChanged += p => _handsFree?.Set(p switch
        {
            WidgetController.Phase.Idle => HandsFree.Mode.Commands,
            WidgetController.Phase.Check => HandsFree.Mode.Checking,
            _ => HandsFree.Mode.Paused,
        });

        var menu = new ContextMenuStrip();
        menu.Items.Add("Settings and sign-in…", null, (_, _) => OpenSettings());
        menu.Items.Add("Open VerbaFlow", null, (_, _) => Open(_settings.Server));
        _handsFreeItem.Click += (_, _) => Saved(_settings with { HandsFree = !_settings.HandsFree });
        menu.Items.Add(_handsFreeItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit the Widget", null, (_, _) => ExitThread());
        _tray = new NotifyIcon { Icon = AppIcon, Text = $"VerbaFlow Widget ({_settings.Keys})", ContextMenuStrip = menu, Visible = true };
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) OpenSettings(); };

        _hotkeys = new HotkeyWindow(OnHotkey);
        Register();
        if (firstRun) OpenSettings();
        else if (_registered && !_settings.HandsFree) Notify($"VerbaFlow Widget is running. Click where you want the words, then press {_settings.Keys}.");
        ApplyHandsFree(false);
    }

    private (HttpClient, VerbaFlowClient) NewClient()
    {
        var http = new HttpClient { BaseAddress = new Uri(_settings.Server + "/"), Timeout = TimeSpan.FromSeconds(60) };
        return (http, new VerbaFlowClient(http, _signIn));
    }

    private void Register(bool quiet = false) => _registered = TryRegister(_settings.Keys, quiet);

    private bool TryRegister(Shortcut keys, bool quiet)
    {
        var mods = Native.MOD_NOREPEAT | (keys.Ctrl ? Native.MOD_CONTROL : 0) | (keys.Alt ? Native.MOD_ALT : 0)
            | (keys.Shift ? Native.MOD_SHIFT : 0) | (keys.Win ? Native.MOD_WIN : 0);
        var ok = Native.RegisterHotKey(_hotkeys.Handle, HotkeyId, mods, keys.VirtualKey);
        if (!ok && !quiet) Notify($"{keys} is taken by another program, so the Widget cannot use it. Choose another shortcut in the Widget's settings.");
        return ok;
    }

    /// <summary>
    /// Changes the shortcut. False (and the old one kept) when another program has the new one.
    /// </summary>
    public bool ChangeShortcut(Shortcut keys)
    {
        if (keys == _settings.Keys && _registered) return true;
        Native.UnregisterHotKey(_hotkeys.Handle, HotkeyId);
        if (TryRegister(keys, true)) { _registered = true; _tray.Text = $"VerbaFlow Widget ({keys})"; return true; }
        Register(true);
        return false;
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
            // The browser Widget's own shortcut is Alt+Shift+D, whichever one this Widget uses.
            var same = _settings.Keys == Shortcut.Default;
            if (same) Native.UnregisterHotKey(_hotkeys.Handle, HotkeyId);
            Native.ReleaseModifiers();
            Native.Press(Native.VK_MENU, Native.VK_SHIFT, Native.VK_D);
            await Task.Delay(200);
            if (same) Register();
            return;
        }
        await _controller.ShortcutAsync(target);
    }

    /// <summary>Starts or stops hands-free listening to match the settings.</summary>
    private void ApplyHandsFree(bool changed)
    {
        _handsFreeItem.Checked = _settings.HandsFree;
        _controller.HandsFree = _settings.HandsFree;
        if (!_settings.HandsFree)
        {
            if (_handsFree is null) return;
            _handsFree.Dispose(); _handsFree = null;
            if (changed) Notify("Hands-free is off.");
            return;
        }
        if (_handsFree is not null) return;
        _handsFree = HandsFree.Start(_settings.Languages, out var problem);
        if (_handsFree is null)
        {
            Notify(problem!);
            _settings = _settings with { HandsFree = false };
            _settings.Save(WidgetSettings.DefaultPath);
            _handsFreeItem.Checked = false; _controller.HandsFree = false;
            return;
        }
        _handsFree.Heard += OnVoice;
        Notify("Hands-free is on. Say \"VerbaFlow, dictate\", \"VerbaFlow, switch to …\" or \"VerbaFlow, open …\".");
    }

    private async void OnVoice(VoiceCommand c)
    {
        try { await VoiceAsync(c); }
        catch (Exception e) { Program.Log(e); Notify("Something went wrong in the Widget. Try again."); }
    }

    private async Task VoiceAsync(VoiceCommand c)
    {
        if (c.Action is VoiceAction.Accept or VoiceAction.SpeakMore or VoiceAction.FixGrammar or VoiceAction.UndoFix or VoiceAction.Cancel)
        {
            _controller.Voice(c);
            return;
        }
        if (!_controller.Idle) return;
        switch (c.Action)
        {
            case VoiceAction.Dictate:
                // Here the Widget itself is used in browsers too: the browser Widget has no hands-free commands.
                if (FocusTarget.Capture() is { } target) await _controller.ShortcutAsync(target);
                break;
            case VoiceAction.SwitchTo:
                if (!Programs.SwitchTo(c.Name!)) Notify($"No open window was found for \"{c.Name}\". Say \"VerbaFlow, open {c.Name}\" to start it.");
                break;
            case VoiceAction.Open:
                if (!Programs.Open(c.Name!)) Notify($"No program called \"{c.Name}\" was found in the Start menu.");
                break;
            case VoiceAction.Key:
                Programs.Press(c.Key!.Value);
                break;
            case VoiceAction.GoToCell:
                await Programs.GoToCellAsync(c.Cell!);
                break;
        }
    }

    public void Notify(string text) => _tray.ShowBalloonTip(6000, "VerbaFlow Widget", text, ToolTipIcon.None);

    private void OpenSettings()
    {
        if (_settingsForm is { IsDisposed: false }) { _settingsForm.Activate(); return; }
        _settingsForm = new SettingsForm(() => _settings, () => _client, _signIn, Saved, ChangeShortcut);
        _settingsForm.Show();
        _settingsForm.Activate();
    }

    /// <summary>New settings: a new address means a new connection to VerbaFlow.</summary>
    private void Saved(WidgetSettings s)
    {
        var serverChanged = s.Server != _settings.Server;
        var handsChanged = s.HandsFree != _settings.HandsFree || (s.HandsFree && s.Languages != _settings.Languages);
        _settings = s;
        s.Save(WidgetSettings.DefaultPath);
        if (serverChanged) { var old = _http; (_http, _client) = NewClient(); old.Dispose(); }
        if (handsChanged) { _handsFree?.Dispose(); _handsFree = null; ApplyHandsFree(true); }
    }

    public static void Open(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch (Exception) { /* no browser */ }
    }

    protected override void ExitThreadCore()
    {
        Native.UnregisterHotKey(_hotkeys.Handle, HotkeyId);
        _handsFree?.Dispose();
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
