namespace VerbaFlow.Widget;

/// <summary>
/// The small window beside the cursor. Red: listening, the words appear as they are heard (grey while still being
/// heard). Amber: check and change the words, then Accept. It is shown without taking the keyboard from the program
/// while listening, and takes it for checking.
/// </summary>
internal sealed class DictationPanel : Form
{
    public static readonly Color Red = Color.FromArgb(176, 42, 55), Amber = Color.FromArgb(166, 106, 0), Green = Color.FromArgb(21, 115, 71);

    private readonly Panel _strip = new() { Dock = DockStyle.Top, Height = 30, Padding = new Padding(10, 0, 10, 0) };
    private readonly Label _state = new() { Dock = DockStyle.Left, AutoSize = true, ForeColor = Color.White, Font = new Font("Segoe UI", 10f, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(0, 6, 0, 0) };
    private readonly Label _note = new() { Dock = DockStyle.Right, AutoSize = true, ForeColor = Color.White, Font = new Font("Segoe UI", 9f), Padding = new Padding(0, 7, 0, 0) };
    private readonly RichTextBox _live = new() { Dock = DockStyle.Fill, ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = Color.White, Font = new Font("Segoe UI", 11f), TabStop = false, ScrollBars = RichTextBoxScrollBars.Vertical };
    private readonly TextBox _edit = new() { Dock = DockStyle.Fill, Multiline = true, AcceptsReturn = true, ScrollBars = ScrollBars.Vertical, Font = new Font("Segoe UI", 11f), Visible = false };
    private readonly Label _message = new() { Dock = DockStyle.Bottom, AutoSize = false, Height = 0, Font = new Font("Segoe UI", 9f), Padding = new Padding(10, 2, 10, 2) };
    private readonly FlowLayoutPanel _buttons = new() { Dock = DockStyle.Bottom, Height = 44, Padding = new Padding(8, 6, 8, 6), WrapContents = false };
    private bool _activate;

    public event Action? StopClicked, CancelClicked, AcceptClicked, SpeakMoreClicked, FixClicked, UndoFixClicked, SettingsClicked;

    public DictationPanel()
    {
        Text = "VerbaFlow Widget";
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        ControlBox = false;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        KeyPreview = true;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(460, 230);
        BackColor = Color.White;
        Icon = TrayApp.AppIcon;
        var middle = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10, 8, 10, 4) };
        middle.Controls.Add(_live);
        middle.Controls.Add(_edit);
        _strip.Controls.Add(_state);
        _strip.Controls.Add(_note);
        Controls.Add(middle);
        Controls.Add(_message);
        Controls.Add(_buttons);
        Controls.Add(_strip);
        _edit.TextChanged += (_, _) => EditChanged?.Invoke();
        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; CancelClicked?.Invoke(); }
            else if (e.KeyCode == Keys.Enter && e.Control && _edit.Visible) { e.SuppressKeyPress = true; AcceptClicked?.Invoke(); }
        };
    }

    public event Action? EditChanged;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string EditText { get => _edit.Text.Replace("\r\n", "\n"); set => _edit.Text = value.Replace("\r\n", "\n").Replace("\n", "\r\n"); }

    /// <summary>Shown while listening without taking the keyboard from the program.</summary>
    protected override bool ShowWithoutActivation => !_activate;

    public void PlaceNear(Rectangle anchor)
    {
        var area = Screen.FromRectangle(anchor).WorkingArea;
        var below = anchor.Bottom + 8 + Height <= area.Bottom;
        var x = Math.Max(area.Left + 8, Math.Min(area.Right - Width - 8, anchor.Left));
        var y = below ? anchor.Bottom + 8 : Math.Max(area.Top + 8, anchor.Top - Height - 8);
        Location = new Point(x, y);
    }

    private void Strip(Color color, string state, string note)
    {
        _strip.BackColor = color;
        _state.Text = state;
        _note.Text = note;
    }

    private void Buttons(params (string Label, Action? Click, bool Primary, bool Enabled)[] buttons)
    {
        _buttons.SuspendLayout();
        _buttons.Controls.Clear();
        foreach (var (label, click, primary, enabled) in buttons)
        {
            var b = new Button { Text = label, AutoSize = true, Enabled = enabled, Font = new Font("Segoe UI", 9.5f, primary ? FontStyle.Bold : FontStyle.Regular), Margin = new Padding(0, 0, 6, 0) };
            if (primary) { b.BackColor = Green; b.ForeColor = Color.White; b.FlatStyle = FlatStyle.Flat; }
            b.Click += (_, _) => click?.Invoke();
            _buttons.Controls.Add(b);
        }
        if (_edit.Visible)
        {
            var s = new LinkLabel { Text = "Settings", AutoSize = true, Margin = new Padding(12, 8, 0, 0) };
            s.LinkClicked += (_, _) => SettingsClicked?.Invoke();
            _buttons.Controls.Add(s);
        }
        _buttons.ResumeLayout();
    }

    public void Message(string text, bool bad = false)
    {
        _message.Text = text;
        _message.ForeColor = bad ? Red : Color.FromArgb(60, 60, 60);
        _message.Height = text.Length == 0 ? 0 : TextRenderer.MeasureText(text, _message.Font, new Size(ClientSize.Width - 20, 0), TextFormatFlags.WordBreak).Height + 6;
    }

    public void ShowStarting()
    {
        _edit.Visible = false; _live.Visible = true;
        Strip(Red, "Starting…", "");
        Live("", "", 0);
        Message("");
        Buttons(("Cancel", () => CancelClicked?.Invoke(), false, true));
        Appear(false);
    }

    public void ShowListening(string before, string heard, int pending, bool lost)
    {
        if (_edit.Visible || _state.Text != "Listening")
        {
            _edit.Visible = false; _live.Visible = true;
            Buttons(("Stop", () => StopClicked?.Invoke(), false, true), ("Cancel", () => CancelClicked?.Invoke(), false, true));
        }
        Strip(Red, "Listening", lost ? "Connection lost: still recording" : "Alt+Shift+D to stop");
        Live(before, heard, pending);
    }

    public void ShowFinishing()
    {
        Strip(Red, "Finishing…", "");
        Buttons(("Cancel", () => CancelClicked?.Invoke(), false, true));
    }

    public void ShowCheck(string text, string message, bool bad, bool canFix, bool canUndoFix, bool amend)
    {
        Strip(Amber, "Check the text, then Accept", "Ctrl+Enter to accept");
        _live.Visible = false; _edit.Visible = true;
        EditText = text;
        Message(message, bad);
        UpdateCheckButtons(canFix, canUndoFix, amend);
        Appear(true);
        _edit.Focus();
        _edit.SelectionStart = _edit.TextLength;
        _edit.ScrollToCaret();
    }

    public void UpdateCheckButtons(bool canFix, bool canUndoFix, bool amend)
    {
        var any = EditText.Trim().Length > 0;
        var list = new List<(string, Action?, bool, bool)> { ("Accept", () => AcceptClicked?.Invoke(), true, any || amend), ("Speak more", () => SpeakMoreClicked?.Invoke(), false, true) };
        if (canUndoFix) list.Add(("Undo fix", () => UndoFixClicked?.Invoke(), false, true));
        else if (canFix) list.Add(("Fix grammar", () => FixClicked?.Invoke(), false, any));
        list.Add(("Cancel", () => CancelClicked?.Invoke(), false, true));
        Buttons([.. list]);
    }

    public void Busy(string message)
    {
        foreach (Control c in _buttons.Controls) c.Enabled = false;
        Message(message);
    }

    public void ShowAdded()
    {
        Strip(Green, "Added", "");
        Buttons();
    }

    private void Appear(bool activate)
    {
        _activate = activate;
        if (!Visible) Show();
        if (activate) { Activate(); BringToFront(); }
    }

    /// <summary>The words so far: those already in the panel, then the heard ones, the last few grey while still being heard.</summary>
    private void Live(string before, string heard, int pending)
    {
        _live.SuspendLayout();
        _live.Clear();
        void Add(string t, Color c) { _live.SelectionStart = _live.TextLength; _live.SelectionLength = 0; _live.SelectionColor = c; _live.AppendText(t); }
        if (before.Trim().Length > 0) Add(before.Trim() + (DictationText.JoinsOn(heard) ? "" : " "), Color.Black);
        var pieces = System.Text.RegularExpressions.Regex.Split(heard, @"(\s+)");
        var wordsLeft = DictationText.Words(heard) - pending;
        var firm = new System.Text.StringBuilder();
        var grey = new System.Text.StringBuilder();
        foreach (var piece in pieces)
        {
            if (piece.Length == 0) continue;
            if (piece.Trim().Length == 0) { (wordsLeft > 0 ? firm : grey).Append(piece); continue; }
            if (wordsLeft-- > 0) firm.Append(piece); else grey.Append(piece);
        }
        Add(firm.ToString(), Color.Black);
        Add(grey.ToString(), Color.Gray);
        if (heard.Length == 0 && before.Trim().Length == 0) Add("Start speaking…", Color.Gray);
        _live.SelectionStart = _live.TextLength;
        _live.ScrollToCaret();
        _live.ResumeLayout();
    }

    /// <summary>Closing the panel with the window's own controls counts as Cancel; the panel is only hidden.</summary>
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; CancelClicked?.Invoke(); }
        base.OnFormClosing(e);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x08; // WS_EX_TOPMOST
            return cp;
        }
    }
}
