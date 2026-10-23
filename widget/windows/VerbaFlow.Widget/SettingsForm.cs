using Microsoft.Win32;

namespace VerbaFlow.Widget;

/// <summary>The Widget's settings: VerbaFlow's address, signing in, the language spoken, paragraphs, and starting with Windows.</summary>
internal sealed class SettingsForm : Form
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run", RunName = "VerbaFlow Widget";

    private readonly Func<VerbaFlowClient> _client;
    private readonly WindowsSignIn _signIn;
    private readonly Action<WidgetSettings> _saved;
    private WidgetSettings _settings;

    private readonly TextBox _server = new() { Width = 320 };
    private readonly Label _status = new() { AutoSize = true, MaximumSize = new Size(440, 0), Padding = new Padding(0, 4, 0, 4) };
    private readonly Button _signInButton = new() { Text = "Sign in with Microsoft", AutoSize = true };
    private readonly Button _signOutButton = new() { Text = "Sign out", AutoSize = true, Visible = false };
    private readonly Label _testUserLabel = new() { Text = "Test user (only on this computer, while nobody is signed in with Microsoft)", AutoSize = true, MaximumSize = new Size(440, 0), Visible = false };
    private readonly ComboBox _testUser = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160, Visible = false };
    private readonly ComboBox _language = new() { DropDownStyle = ComboBoxStyle.DropDown, Width = 320 };
    private readonly ComboBox _gap = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 320 };
    private readonly CheckBox _browsers = new() { Text = "In Chrome and Edge, leave Alt+Shift+D to the browser Widget", AutoSize = true };
    private readonly CheckBox _startWithWindows = new() { Text = "Start the Widget when I sign in to Windows", AutoSize = true };
    private List<SpokenLanguage> _languages = [];

    public SettingsForm(WidgetSettings settings, Func<VerbaFlowClient> client, WindowsSignIn signIn, Action<WidgetSettings> saved)
    {
        _settings = settings; _client = client; _signIn = signIn; _saved = saved;
        Text = "VerbaFlow Widget settings";
        Icon = TrayApp.AppIcon;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Font = new Font("Segoe UI", 9.5f);

        var flow = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, Padding = new Padding(16), WrapContents = false };
        Label Heading(string t) => new() { Text = t, AutoSize = true, Font = new Font("Segoe UI", 10.5f, FontStyle.Bold), Margin = new Padding(0, 12, 0, 4) };
        Label Note(string t) => new() { Text = t, AutoSize = true, MaximumSize = new Size(440, 0), ForeColor = Color.FromArgb(80, 80, 80) };

        flow.Controls.Add(Note("Click where you want the words in any program (Outlook, Word, Teams…), press Alt+Shift+D and speak. " +
            "Press Alt+Shift+D again (or Stop) when you are done, check the words, then Accept."));
        flow.Controls.Add(Heading("1. VerbaFlow"));
        flow.Controls.Add(Note("VerbaFlow's address. On this computer: http://localhost:5044 (VerbaFlow must be running)."));
        flow.Controls.Add(_server);
        var check = new Button { Text = "Save and check", AutoSize = true };
        check.Click += async (_, _) => await SaveAndCheckAsync();
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 4, 0, 0) };
        row.Controls.AddRange([check, _signInButton, _signOutButton]);
        flow.Controls.Add(row);
        flow.Controls.Add(_status);
        flow.Controls.Add(_testUserLabel);
        flow.Controls.Add(_testUser);

        flow.Controls.Add(Heading("2. The language you speak"));
        flow.Controls.Add(_language);
        flow.Controls.Add(Heading("3. New paragraphs"));
        flow.Controls.Add(Note("Word puts space after each paragraph itself; most email and chat boxes do not, so an empty line is left there."));
        flow.Controls.Add(_gap);
        flow.Controls.Add(Heading("4. Other"));
        flow.Controls.Add(_browsers);
        flow.Controls.Add(_startWithWindows);

        var save = new Button { Text = "Save", AutoSize = true, Font = new Font("Segoe UI", 9.5f, FontStyle.Bold), Margin = new Padding(0, 16, 6, 0) };
        save.Click += (_, _) => { if (Save()) Close(); };
        var close = new Button { Text = "Close", AutoSize = true, Margin = new Padding(0, 16, 0, 0) };
        close.Click += (_, _) => Close();
        var bottom = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        bottom.Controls.AddRange([save, close]);
        flow.Controls.Add(bottom);
        Controls.Add(flow);
        AcceptButton = save;

        _server.Text = settings.Server;
        _gap.Items.AddRange(["Automatic: an empty line between paragraphs, except in Word", "Always an empty line between paragraphs", "Never: just a new line"]);
        _gap.SelectedIndex = (int)settings.ParagraphGap;
        _browsers.Checked = settings.LeaveBrowsersToExtension;
        _startWithWindows.Checked = StartsWithWindows();
        _language.Text = settings.Languages;
        _testUser.Items.AddRange(["alice", "bob", "carol", "dave"]);
        _testUser.SelectedItem = settings.TestUser;
        if (_testUser.SelectedIndex < 0) _testUser.SelectedIndex = 0;

        _signInButton.Click += async (_, _) => await SignInAsync();
        _signOutButton.Click += async (_, _) => await SignOutAsync();
        Shown += async (_, _) => await CheckAsync();
    }

    private bool Save()
    {
        var server = WidgetSettings.CleanServer(_server.Text);
        if (server is null) { Status("That is not a web address. On this computer it is http://localhost:5044.", true); return false; }
        var language = _languages.FirstOrDefault(l => l.Name == _language.Text)?.Code ?? _language.Text.Trim();
        _settings = _settings with
        {
            Server = server, Languages = language, ParagraphGap = (ParagraphGap)Math.Max(0, _gap.SelectedIndex),
            LeaveBrowsersToExtension = _browsers.Checked, TestUser = _testUser.SelectedItem as string ?? "alice",
        };
        _settings = _settings.Clean();
        _server.Text = _settings.Server;
        _saved(_settings);
        SetStartWithWindows(_startWithWindows.Checked);
        return true;
    }

    private async Task SaveAndCheckAsync() { if (Save()) await CheckAsync(); }

    /// <summary>Whether VerbaFlow answers, how it signs people in, and who the Widget is signed in as.</summary>
    private async Task CheckAsync()
    {
        Status("Checking…", false);
        try
        {
            var client = _client();
            var info = await client.SignInInfoAsync();
            var account = await _signIn.AccountAsync(info);
            _signInButton.Visible = info.Microsoft is not null && account is null;
            _signOutButton.Visible = account is not null;
            _testUser.Visible = _testUserLabel.Visible = info.TestUsers && account is null;
            await LoadLanguagesAsync(client);
            var (name, admin) = await client.MeAsync();
            Status(account is not null ? $"Connected. Signed in with Microsoft as {name}{(admin ? " (admin)" : "")}."
                : $"Connected, as the test user {name}{(admin ? " (admin)" : "")}." + (info.Microsoft is not null ? " Sign in with Microsoft to use your own account." : ""), false);
        }
        catch (SignInNeededException e) { Status(e.Message.StartsWith("Sign in first") ? "Connected. Now click Sign in with Microsoft." : e.Message, true); }
        catch (WidgetException e) { Status(e.Message, true); }
        catch (Exception) { Status("Something went wrong in the Widget. Try again.", true); }
    }

    private async Task LoadLanguagesAsync(VerbaFlowClient client)
    {
        if (_languages.Count > 0) return;
        try
        {
            _languages = (await client.LanguagesAsync()).Where(l => l.Supported).ToList();
            var current = _settings.Languages;
            _language.Items.Clear();
            _language.Items.AddRange([.. _languages.Select(l => l.Name)]);
            _language.Text = _languages.FirstOrDefault(l => l.Code.Equals(current, StringComparison.OrdinalIgnoreCase))?.Name ?? current;
        }
        catch (WidgetException) { /* the code can still be typed */ }
    }

    private async Task SignInAsync()
    {
        if (!Save()) return;
        Status("Microsoft's sign-in page opens in your browser. Sign in there, then come back here.", false);
        try
        {
            var info = await _client().SignInInfoAsync();
            await _signIn.SignInAsync(info, Handle, CancellationToken.None);
            Activate();
            await CheckAsync();
        }
        catch (WidgetException e) { Status(e.Message, true); }
        catch (Exception) { Status("The Microsoft sign-in did not finish. Try again.", true); }
    }

    private async Task SignOutAsync()
    {
        try { await _signIn.SignOutAsync(await _client().SignInInfoAsync()); } catch (Exception) { /* checked below */ }
        await CheckAsync();
    }

    private void Status(string text, bool bad)
    {
        _status.Text = text;
        _status.ForeColor = bad ? DictationPanel.Red : DictationPanel.Green;
    }

    private static bool StartsWithWindows()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey);
        return k?.GetValue(RunName) is string;
    }

    private static void SetStartWithWindows(bool on)
    {
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKey);
            if (on) k.SetValue(RunName, $"\"{Environment.ProcessPath}\"");
            else if (k.GetValue(RunName) is not null) k.DeleteValue(RunName);
        }
        catch (Exception) { /* not allowed on this computer */ }
    }
}
