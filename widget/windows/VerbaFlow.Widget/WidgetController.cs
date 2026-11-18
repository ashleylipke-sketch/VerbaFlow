namespace VerbaFlow.Widget;

/// <summary>
/// One dictation at a time: the shortcut starts listening where the cursor is, the words come back live, the person
/// checks them, and Accept pastes them into the program. Nothing heard is kept: the sound and the words are dropped
/// when the dictation is accepted or cancelled, and VerbaFlow keeps only facts (program, times, counts).
/// </summary>
internal sealed class WidgetController : IDisposable
{
    public enum Phase { Idle, Starting, Listening, Finishing, Check, Busy }

    private readonly Func<VerbaFlowClient> _client;
    private readonly Func<WidgetSettings> _settings;
    private readonly Action<string> _notify;
    private readonly Action _openSettings;
    private readonly Action<WidgetSettings> _save;
    private readonly DictationPanel _panel = new();
    private readonly SynchronizationContext _ui;

    private Phase _phaseValue;
    private Phase _phase
    {
        get => _phaseValue;
        set { if (_phaseValue == value) return; _phaseValue = value; PhaseChanged?.Invoke(value); }
    }

    /// <summary>The dictation moved on (hands-free listens for other commands in each step).</summary>
    public event Action<Phase>? PhaseChanged;

    /// <summary>Hands-free: "stop dictating" finishes, and the panel says what to say.</summary>
    public bool HandsFree { get => _panel.HandsFree; set => _panel.HandsFree = value; }

    public bool Idle => _phase == Phase.Idle;
    private FocusTarget? _target;
    private Guid? _widgetId;
    private LiveListener? _listener;
    private Microphone? _mic;
    private string _prefix = "";
    private bool _amend, _lost;
    private LiveText _text = Empty;
    private string? _beforeFix, _failure;
    private int _spokenMs, _reconnects;
    private float _loudest;
    private DateTime _listenStarted;

    private static readonly LiveText Empty = new([], 0, false);

    public WidgetController(Func<VerbaFlowClient> client, Func<WidgetSettings> settings, Action<WidgetSettings> save, Action<string> notify, Action openSettings)
    {
        _client = client; _settings = settings; _save = save; _notify = notify; _openSettings = openSettings;
        _ui = SynchronizationContext.Current ?? throw new InvalidOperationException("Start the Widget on the window thread.");
        _panel.StopClicked += () => _ = StopAsync();
        _panel.CancelClicked += () => _ = CancelAsync();
        _panel.AcceptClicked += () => _ = AcceptAsync();
        _panel.SpeakMoreClicked += () => _ = SpeakMoreAsync();
        _panel.FixClicked += () => _ = FixGrammarAsync();
        _panel.UndoFixClicked += UndoFix;
        _panel.SettingsClicked += _openSettings;
        _panel.MethodChosen += ChooseMethod;
        _panel.EditChanged += () => { if (_phase == Phase.Check) _panel.UpdateCheckButtons(_widgetId is not null, _beforeFix is not null, _amend); };
    }

    /// <summary>The shortcut: start in the program in front, or stop listening.</summary>
    public async Task ShortcutAsync(FocusTarget target)
    {
        switch (_phase)
        {
            case Phase.Listening: await StopAsync(); return;
            case Phase.Idle: break;
            default: _panel.Activate(); return;
        }
        if (target.IsPassword) { _notify("The Widget does not work in password boxes."); return; }
        _target = target;
        _amend = target.SelectedText.Trim().Length > 0;
        _prefix = _amend ? target.SelectedText : "";
        _widgetId = null; _spokenMs = 0; _reconnects = 0;
        _panel.ShortcutText = _settings().Keys.ToString();
        _panel.PlaceNear(target.Anchor);
        await ListenAsync();
    }

    private async Task ListenAsync()
    {
        _phase = Phase.Starting;
        _text = Empty; _lost = false; _beforeFix = null; _failure = null; _loudest = 0;
        _panel.ShowStarting();
        try
        {
            var client = _client();
            var s = _widgetId is { } id ? await client.ListenAgainAsync(id) : await client.StartAsync(_target!.Program, "Windows program", _settings().Languages);
            _widgetId = s.Id;
            if (_phase != Phase.Starting) { End(false, null); return; } // cancelled meanwhile
            var listener = new LiveListener(client, s);
            listener.Text += t => _ui.Post(_ => { if (_listener == listener) { _text = t; Render(); } }, null);
            listener.Link += live => _ui.Post(_ => { if (_listener == listener && _lost != !live) { _lost = !live; Render(); } }, null);
            listener.Failed += m => _ui.Post(_ => { if (_listener == listener) { _failure = m; _ = StopAsync(); } }, null);
            var mic = new Microphone(b => listener.Write(b.Span));
            mic.Level += l => { if (l > _loudest) _loudest = l; };
            try { mic.Start(); }
            catch { mic.Dispose(); await listener.DisposeAsync(); throw; }
            _listener = listener; _mic = mic;
            _listenStarted = DateTime.UtcNow;
            listener.Start();
            _phase = Phase.Listening;
            Render();
            _ = QuietCheckAsync(listener);
        }
        catch (Exception e)
        {
            if (_phase != Phase.Starting) return;
            if (e is SignInNeededException) _openSettings();
            ShowCheck(_prefix, e is WidgetException ? e.Message : "Something went wrong in the Widget. Try again.", true);
        }
    }

    /// <summary>After a few seconds of nothing but silence, a note: the microphone may be muted or the wrong one.</summary>
    private async Task QuietCheckAsync(LiveListener listener)
    {
        await Task.Delay(4000);
        if (_listener == listener && _phase == Phase.Listening && _loudest < 0.01f)
            _panel.Message("Nothing heard yet. Check that the microphone is on and is the one Windows uses.", true);
    }

    private void Render()
    {
        if (_phase != Phase.Listening) return;
        var heard = DictationText.Heard(_text.Segments, _text.StartsWithBreak);
        _panel.ShowListening(_prefix, VoiceCommands.WithoutStop(heard) ?? heard, _text.PendingWords, _lost);
        if (_loudest >= 0.01f) _panel.Message("");
        // Hands-free: "stop dictating" at the end of what was heard finishes, as the shortcut or Stop would.
        if (HandsFree && VoiceCommands.WithoutStop(heard) is not null) _ = StopAsync();
    }

    private async Task StopAsync()
    {
        if (_phase != Phase.Listening) return;
        _phase = Phase.Finishing;
        _panel.ShowFinishing();
        _mic?.Dispose(); _mic = null;
        var listener = _listener!;
        listener.Stop();
        var r = await listener.Stopped;
        _spokenMs += r.SpokenMs; _reconnects += r.Reconnects;
        await listener.DisposeAsync();
        if (_listener != listener || _phase != Phase.Finishing) return; // cancelled meanwhile
        _listener = null;
        var heardText = DictationText.Heard(_text.Segments, _text.StartsWithBreak);
        var text = DictationText.Join(_prefix, HandsFree ? VoiceCommands.WithoutStop(heardText) ?? heardText : heardText);
        if (_failure is not null) ShowCheck(text, _failure, true);
        else if (!r.Complete) ShowCheck(text, "The last words may be missing: VerbaFlow could not be reached to finish them.", true);
        else ShowCheck(text, "", false);
    }

    private void ShowCheck(string text, string message, bool bad)
    {
        _phase = Phase.Check;
        _panel.ShowCheck(text, message, bad, _widgetId is not null, _beforeFix is not null, _amend);
        ShowMethod();
    }

    /// <summary>How the words will go in, under the text: the way for this program, or why they will be copied.</summary>
    private void ShowMethod()
    {
        if (_target is not { } t) return;
        _panel.ShowMethod(t.Program, _settings().MethodFor(t.Program), t.CopyReason);
    }

    /// <summary>The person chose another way for this program: it is remembered for next time.</summary>
    private void ChooseMethod(InsertMethod method)
    {
        if (_target is not { } t) return;
        _save(_settings().WithMethod(t.Program, method));
        ShowMethod();
    }

    /// <summary>A hands-free command for the words being checked.</summary>
    public void Voice(VoiceCommand command)
    {
        if (_phase != Phase.Check) return;
        switch (command.Action)
        {
            case VoiceAction.Accept: _ = AcceptAsync(); break;
            case VoiceAction.SpeakMore: _ = SpeakMoreAsync(); break;
            case VoiceAction.FixGrammar: _ = FixGrammarAsync(); break;
            case VoiceAction.UndoFix: UndoFix(); break;
            case VoiceAction.Cancel: _ = CancelAsync(); break;
        }
    }

    private async Task SpeakMoreAsync()
    {
        if (_phase != Phase.Check) return;
        _prefix = _panel.EditText;
        await ListenAsync();
    }

    /// <summary>AI correction of grammar and punctuation, shown to check before Accept; Undo fix puts the text back.</summary>
    private async Task FixGrammarAsync()
    {
        if (_phase != Phase.Check || _widgetId is not { } id) return;
        var text = _panel.EditText;
        _phase = Phase.Busy;
        _panel.Busy("Checking grammar and punctuation…");
        try
        {
            var r = await _client().FixGrammarAsync(id, text);
            if (_phase != Phase.Busy) return;
            if (r.LinesChanged == 0) { ShowCheck(text, "No corrections needed.", false); return; }
            _beforeFix = text;
            ShowCheck(r.Text, $"Corrected {r.LinesChanged} line{(r.LinesChanged == 1 ? "" : "s")}. Check the text, then Accept.", false);
        }
        catch (WidgetException e) { if (_phase == Phase.Busy) ShowCheck(text, e.Message, true); }
    }

    private void UndoFix()
    {
        if (_phase != Phase.Check || _beforeFix is null) return;
        var before = _beforeFix;
        _beforeFix = null;
        ShowCheck(before, "The text is back as it was.", false);
    }

    private async Task AcceptAsync()
    {
        if (_phase != Phase.Check || _target is not { } target) return;
        var text = _panel.EditText.TrimEnd();
        if (text.Trim().Length == 0 && !_amend) return;
        _phase = Phase.Busy;
        var pasted = DictationText.ForPasting(text, DictationText.EmptyLineFor(_settings().ParagraphGap, target.Program));
        if (!_amend) pasted = DictationText.Spaced(target.TextBefore, pasted);
        End(true, DictationText.Words(text));
        _panel.Hide();
        var method = target.CopyReason is not null ? InsertMethod.Copy : _settings().MethodFor(target.Program);
        var ok = pasted.Length == 0 ? method == InsertMethod.Copy || DeleteSelection(target) : await Inserter.PutAsync(target, pasted, method);
        if (ok && method == InsertMethod.Copy && pasted.Length > 0) _notify("The words are copied. Click where you want them and paste with Ctrl+V.");
        if (!ok)
        {
            Inserter.Copy(pasted);
            _notify("The window was closed, so the words were copied instead. Paste them where you want them with Ctrl+V.");
        }
        Reset();
    }

    private static bool DeleteSelection(FocusTarget target)
    {
        if (!Native.IsWindow(target.Window)) return false;
        Native.SetForegroundWindow(target.Window);
        Native.ReleaseModifiers();
        Native.Press(Native.VK_DELETE);
        return true;
    }

    private async Task CancelAsync()
    {
        if (_phase == Phase.Idle) return;
        var listener = _listener;
        _mic?.Dispose(); _mic = null;
        End(false, null);
        _listener = null;
        _panel.Hide();
        if (_target is { } t && Native.IsWindow(t.Window)) Native.SetForegroundWindow(t.Window);
        Reset();
        if (listener is not null) await listener.DisposeAsync();
    }

    /// <summary>Tells VerbaFlow the dictation ended (counts only); it then throws away the live session and its words.</summary>
    private void End(bool accepted, int? words)
    {
        if (_widgetId is not { } id) return;
        var spoken = _spokenMs + (_listener?.SpokenMs ?? 0);
        var reconnects = _reconnects + (_listener?.Reconnects ?? 0);
        var client = _client();
        _ = Task.Run(async () => { try { await client.EndAsync(id, accepted, spoken, words, reconnects); } catch (WidgetException) { /* VerbaFlow forgets it in time */ } });
        _widgetId = null;
    }

    private void Reset()
    {
        _phase = Phase.Idle;
        _target = null; _prefix = ""; _text = Empty; _beforeFix = null; _failure = null; _amend = false;
        _panel.EditText = "";
    }

    public void Dispose()
    {
        _mic?.Dispose();
        End(false, null);
        _listener?.DisposeAsync().AsTask().Wait(2000);
        _panel.Dispose();
    }
}
