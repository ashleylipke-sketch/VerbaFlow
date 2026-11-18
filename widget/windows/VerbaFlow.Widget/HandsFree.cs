using System.Globalization;
using System.Speech.Recognition;

namespace VerbaFlow.Widget;

/// <summary>
/// Hands-free listening, done by Windows' own speech recognition on this computer: nothing is sent to VerbaFlow or
/// Azure, and it costs nothing. It listens only for the Widget's commands (a fixed list, which makes it accurate and
/// keeps ordinary talk from being taken as a command), and is paused while the person dictates.
/// </summary>
internal sealed class HandsFree : IDisposable
{
    public enum Mode { Commands, Checking, Paused }

    private readonly SpeechRecognitionEngine _engine;
    private readonly SynchronizationContext _ui;
    private readonly System.Threading.Timer _refresh;
    private Grammar? _commands, _checking;
    private string _names = "";
    private Mode _mode = Mode.Commands;
    private bool _running, _stopping, _disposed;

    /// <summary>A command heard (on the window thread).</summary>
    public event Action<VoiceCommand>? Heard;

    private HandsFree(SpeechRecognitionEngine engine)
    {
        _engine = engine;
        _ui = SynchronizationContext.Current ?? throw new InvalidOperationException("Start hands-free on the window thread.");
        _engine.SpeechRecognized += OnRecognized;
        // Listening stops a moment after it is asked to; only then can it start again.
        _engine.RecognizeCompleted += (_, _) => _ui.Post(_ => { _stopping = false; try { Resume(); } catch (Exception e) { Program.Log(e); } }, null);
        _engine.SetInputToDefaultAudioDevice();
        LoadGrammars();
        // Programs opened or closed since: the names to listen for are brought up to date.
        _refresh = new System.Threading.Timer(_ => _ui.Post(_ => { try { LoadGrammars(); } catch (Exception e) { Program.Log(e); } }, null), null, 15_000, 15_000);
    }

    /// <summary>
    /// Hands-free listening, or null with a plain sentence when this computer has no English speech recognition or
    /// no microphone.
    /// </summary>
    public static HandsFree? Start(string languages, out string? problem)
    {
        problem = null;
        try
        {
            var installed = SpeechRecognitionEngine.InstalledRecognizers();
            var wanted = languages.Split(',').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("en", StringComparison.OrdinalIgnoreCase)) ?? "en-GB";
            var info = installed.FirstOrDefault(r => r.Culture.Name.Equals(wanted, StringComparison.OrdinalIgnoreCase))
                ?? installed.FirstOrDefault(r => r.Culture.TwoLetterISOLanguageName == "en");
            if (info is null)
            {
                problem = "Hands-free needs Windows speech recognition in English, which this computer does not have. In Windows Settings, go to " +
                    "Time & language › Language & region, add English (United Kingdom) and tick Speech recognition, then switch hands-free on again.";
                return null;
            }
            var hands = new HandsFree(new SpeechRecognitionEngine(info));
            hands.Resume();
            return hands;
        }
        catch (Exception e)
        {
            Program.Log(e);
            problem = "Hands-free could not start listening. Check that a microphone is connected and that Windows lets apps use it " +
                "(Settings › Privacy & security › Microphone).";
            return null;
        }
    }

    /// <summary>What to listen for: commands, the check step's commands, or nothing while dictating.</summary>
    public void Set(Mode mode)
    {
        if (mode == _mode) return;
        _mode = mode;
        if (mode == Mode.Paused) { Pause(); return; }
        if (_commands is not null) _commands.Enabled = mode == Mode.Commands;
        if (_checking is not null) _checking.Enabled = mode == Mode.Checking;
        Resume();
    }

    private void Resume()
    {
        if (_disposed || _running || _stopping || _mode == Mode.Paused) return;
        _engine.RecognizeAsync(RecognizeMode.Multiple);
        _running = true;
    }

    private void Pause()
    {
        if (!_running) return;
        _stopping = true;
        _running = false;
        _engine.RecognizeAsyncCancel();
    }

    private void LoadGrammars()
    {
        if (_disposed) return;
        var (open, start) = Programs.Names();
        var key = string.Join("|", open) + "#" + string.Join("|", start);
        if (key == _names) return;
        _names = key;
        var culture = _engine.RecognizerInfo.Culture;
        var commands = Commands(open, start, culture);
        var checking = Checking(culture);
        void Swap()
        {
            if (_commands is not null) _engine.UnloadGrammar(_commands);
            if (_checking is not null) _engine.UnloadGrammar(_checking);
            _commands = commands; _checking = checking;
            _engine.LoadGrammar(commands);
            _engine.LoadGrammar(checking);
            commands.Enabled = _mode != Mode.Checking;
            checking.Enabled = _mode == Mode.Checking;
        }
        if (_running)
        {
            void Update(object? s, RecognizerUpdateReachedEventArgs e) { _engine.RecognizerUpdateReached -= Update; Swap(); }
            _engine.RecognizerUpdateReached += Update;
            _engine.RequestRecognizerUpdate();
        }
        else Swap();
    }

    /// <summary>"verba flow" followed by one command.</summary>
    private static Grammar Commands(List<string> open, List<string> start, CultureInfo culture)
    {
        var choices = new Choices();
        foreach (var p in VoiceCommands.DictatePhrases.Concat(VoiceCommands.KeyPhrases.Keys)) choices.Add(p);
        if (open.Count > 0)
        {
            var g = new GrammarBuilder(new Choices([.. VoiceCommands.SwitchWords]));
            g.Append(new Choices([.. open]));
            choices.Add(g);
        }
        if (start.Count > 0)
        {
            var g = new GrammarBuilder(new Choices([.. VoiceCommands.OpenWords]));
            g.Append(new Choices([.. start]));
            choices.Add(g);
        }
        // "go to cell B 5": a letter or two, then a number.
        var letters = new Choices(Enumerable.Range('a', 26).Select(c => ((char)c).ToString()).ToArray());
        var numbers = new Choices(Enumerable.Range(1, 1000).Select(n => n.ToString(CultureInfo.InvariantCulture)).ToArray());
        var cell = new GrammarBuilder("go to");
        cell.Append("cell", 0, 1);
        cell.Append(letters);
        cell.Append(letters, 0, 1);
        cell.Append(numbers);
        choices.Add(cell);
        var all = new GrammarBuilder(VoiceCommands.WakePhrase) { Culture = culture };
        all.Append(choices);
        return new Grammar(all) { Name = "commands" };
    }

    private static Grammar Checking(CultureInfo culture)
    {
        var g = new GrammarBuilder { Culture = culture };
        g.Append(VoiceCommands.WakePhrase, 0, 1);
        g.Append(new Choices([.. VoiceCommands.CheckPhrases.Keys]));
        return new Grammar(g) { Name = "checking" };
    }

    private void OnRecognized(object? sender, SpeechRecognizedEventArgs e)
    {
        // A low score is more likely other talk in the room than a command.
        if (e.Result.Confidence < 0.6f) return;
        var checking = e.Result.Grammar?.Name == "checking";
        var command = VoiceCommands.Parse(e.Result.Text, checking);
        if (command is null) return;
        _ui.Post(_ => Heard?.Invoke(command), null);
    }

    public void Dispose()
    {
        _refresh.Dispose();
        _mode = Mode.Paused;
        _disposed = true;
        try { Pause(); } catch (Exception) { /* already stopped */ }
        _engine.Dispose();
    }
}
