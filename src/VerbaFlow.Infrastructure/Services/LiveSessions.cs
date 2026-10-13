using System.Collections.Concurrent;
using System.Security.Cryptography;
using VerbaFlow.Core.Domain;
using VerbaFlow.Core.Providers;
using VerbaFlow.Core.Transcripts;
using VerbaFlow.Infrastructure.Azure;

namespace VerbaFlow.Infrastructure.Services;

/// <summary>
/// Speech mode: the text heard live, kept with the item it was saved as. Used as the item's text when it covers the
/// whole recording and the live service finished cleanly; otherwise the text is worked out again from the recording.
/// </summary>
/// <param name="Phrases">The phrases as the speech service heard them (spoken commands not yet applied).</param>
/// <param name="Finished">The live service was told the recording had ended and finished its last phrase.</param>
/// <param name="CoveredMs">How much of the recording reached the live service.</param>
/// <param name="Reconnects">How often the connection was lost and made again while speaking.</param>
/// <param name="FailureReference">Set when the live service stopped working (the VF- reference of the report).</param>
public sealed record LiveTranscript(Guid ItemId, string Engine, IReadOnlyList<Segment> Phrases, bool Finished, int CoveredMs,
    int Reconnects, string? FailureReference)
{
    /// <summary>Why the live text cannot stand as the item's text, in plain words, or null when it can.</summary>
    public string? Problem(int lengthMs) =>
        FailureReference is not null ? $"the live speech service stopped ({FailureReference})"
        : !Finished ? "the live text was not finished (the connection was lost at the end)"
        : CoveredMs < lengthMs - 1500 ? "part of the recording did not reach the live speech service"
        : Phrases.Count == 0 ? "no words were heard live"
        : null;
}

/// <summary>What a dictation's spoken commands need: the words to tell the speech service to expect, and the saved text for "insert".</summary>
public static class DictationContext
{
    public static async Task<(List<string> Phrases, IReadOnlyList<SavedText> Saved)> LoadAsync(Stores stores)
    {
        var phrases = (await stores.Vocabulary.ListAsync()).Select(v => v.Text).ToList();
        // Saved text a dictation can drop in with "insert <code>"; its codes help the speech service hear them.
        var saved = await stores.SavedText.ListAsync();
        phrases.AddRange(saved.Select(t => "insert " + t.Code).Distinct(StringComparer.OrdinalIgnoreCase).Take(Math.Max(0, VocabularyService.MaxTerms - phrases.Count)));
        // The list commands are easy to mishear ("end list" came back as "endless"), so the speech service is told to expect them.
        phrases.AddRange(new[] { "bullet point", "numbered point", "end list" }.Take(Math.Max(0, VocabularyService.MaxTerms - phrases.Count)));
        return (phrases, saved);
    }

    /// <summary>The phrases with the spoken commands applied, as they will be shown (live) and saved.</summary>
    public static IReadOnlyList<Segment> Apply(IEnumerable<Segment> phrases, IReadOnlyList<SavedText> saved, IReadOnlyList<string> names) =>
        DictationCommands.ApplyCounted(AzureSpeechService.MergeTurns(phrases), saved.Select(t => (t.Code, t.Text)).ToList(), names,
            spokenPunctuationOnly: true).Segments;
}

/// <summary>One person speaking in Speech mode. It lives across lost connections until the recording is saved.</summary>
public sealed class LiveSession
{
    private readonly object _gate = new();
    private readonly List<Segment> _phrases = [];
    private int _generation, _fromMs, _reconnects, _connections;
    private long _bytes;
    private bool _finished;
    private string? _failure;
    private CancellationTokenSource? _current;

    public Guid Id { get; } = Guid.NewGuid();
    public required Guid OwnerId { get; init; }
    public required string[] Languages { get; init; }
    public required string Engine { get; init; }
    public required List<string> Phrases { get; init; }
    public required IReadOnlyList<SavedText> Saved { get; init; }
    public DateTimeOffset LastUsed { get; set; }

    /// <summary>
    /// A connection starts (again) with audio from <paramref name="fromMs"/>. Phrases heard after that point are dropped,
    /// since the audio is sent again, and any earlier connection is told to stop.
    /// </summary>
    public (int Generation, CancellationToken Stop) Connect(int fromMs)
    {
        lock (_gate)
        {
            _current?.Cancel();
            _current = new CancellationTokenSource();
            if (_connections++ > 0) _reconnects++;
            _fromMs = Math.Max(0, fromMs);
            _bytes = 0;
            _finished = false;
            _phrases.RemoveAll(p => p.EndMs > _fromMs + 50);
            return (++_generation, _current.Token);
        }
    }

    /// <summary>Where the audio of the current connection starts, so its times can be put on the recording's clock.</summary>
    public int Offset(int generation) { lock (_gate) return generation == _generation ? _fromMs : -1; }

    public void Heard(int generation, int bytes) { lock (_gate) if (generation == _generation) _bytes += bytes; }

    public bool AddFinal(int generation, Segment phrase)
    {
        lock (_gate)
        {
            if (generation != _generation) return false;
            _phrases.Add(phrase);
            return true;
        }
    }

    public void Finish(int generation) { lock (_gate) if (generation == _generation) _finished = true; }
    public void Fail(int generation, string reference) { lock (_gate) if (generation == _generation) _failure = reference; }
    public bool Failed { get { lock (_gate) return _failure is not null; } }

    /// <summary>The finished phrases with spoken commands applied, and where the last one ends (to carry on from after a lost connection).</summary>
    public (IReadOnlyList<Segment> Text, int HeardToMs) Snapshot()
    {
        List<Segment> phrases;
        lock (_gate) phrases = [.. _phrases];
        return (DictationContext.Apply(phrases, Saved, Phrases), phrases.Count == 0 ? 0 : phrases.Max(p => p.EndMs));
    }

    /// <summary>The finished phrases as heard, commands not yet applied.</summary>
    public List<Segment> PhrasesSoFar() { lock (_gate) return [.. _phrases]; }

    public LiveTranscript ToTranscript(Guid itemId)
    {
        lock (_gate)
            return new LiveTranscript(itemId, Engine, [.. _phrases.OrderBy(p => p.StartMs)], _finished, _fromMs + (int)(_bytes / 32), _reconnects, _failure);
    }

    public void Close() { lock (_gate) _current?.Cancel(); }
}

/// <summary>
/// Live sessions in memory. A browser opens one, then connects to it with a single-use ticket (a browser cannot send the
/// sign-in header on a live connection). Sessions not used for six hours are forgotten.
/// </summary>
public sealed class LiveSessions(Stores stores, ILiveSpeech live, TimeProvider clock)
{
    private readonly ConcurrentDictionary<Guid, LiveSession> _sessions = new();
    private readonly ConcurrentDictionary<string, (Guid Session, DateTimeOffset Until)> _tickets = new();

    public string EngineName => live.Name;

    public async Task<(LiveSession Session, string Ticket)> StartAsync(User user, string? spokenLanguages)
    {
        Tidy();
        var languages = (spokenLanguages ?? "").Split(new[] { ',', '+' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(SpokenLanguageCatalogue.Normalise).Where(x => x is not null).Select(x => x!).Distinct().ToArray();
        if (languages.Contains("auto") || languages.Length == 0)
            throw new DomainException("Choose the language you will speak. Speech mode cannot detect it automatically.");
        if (languages.Length > SpokenLanguageCatalogue.MaxPerRecording)
            throw new DomainException($"Choose at most {SpokenLanguageCatalogue.MaxPerRecording} languages.");
        var (phrases, saved) = await DictationContext.LoadAsync(stores);
        var s = new LiveSession { OwnerId = user.Id, Languages = languages, Engine = live.Name, Phrases = phrases, Saved = saved, LastUsed = clock.GetUtcNow() };
        _sessions[s.Id] = s;
        return (s, Ticket(s));
    }

    public string NewTicket(User user, Guid id) => Ticket(Require(user, id));

    /// <summary>The session a ticket is for, or null. A ticket works once and only for a minute.</summary>
    public LiveSession? Redeem(Guid id, string? ticket)
    {
        if (string.IsNullOrEmpty(ticket) || !_tickets.TryRemove(ticket, out var t)) return null;
        if (t.Session != id || t.Until < clock.GetUtcNow() || !_sessions.TryGetValue(id, out var s)) return null;
        s.LastUsed = clock.GetUtcNow();
        return s;
    }

    /// <summary>The session's text for saving with the recording; the session is closed and forgotten.</summary>
    public LiveTranscript? Take(User user, Guid id, Guid itemId)
    {
        if (!_sessions.TryGetValue(id, out var s) || s.OwnerId != user.Id) return null;
        _sessions.TryRemove(id, out _);
        s.Close();
        return s.ToTranscript(itemId);
    }

    public void Discard(User user, Guid id)
    {
        if (_sessions.TryGetValue(id, out var s) && s.OwnerId == user.Id && _sessions.TryRemove(id, out _)) s.Close();
    }

    public Task<ILiveRecognition> RecogniseAsync(LiveSession s, int fromMs, CancellationToken ct) => live.StartAsync(new LiveOptions(s.Languages, s.Phrases, fromMs), ct);

    private LiveSession Require(User user, Guid id)
    {
        if (!_sessions.TryGetValue(id, out var s)) throw new NotFoundException("That live session has ended.");
        if (s.OwnerId != user.Id) throw new ForbiddenException("This live session belongs to someone else.");
        s.LastUsed = clock.GetUtcNow();
        return s;
    }

    private string Ticket(LiveSession s)
    {
        var ticket = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        _tickets[ticket] = (s.Id, clock.GetUtcNow().AddMinutes(1));
        return ticket;
    }

    private void Tidy()
    {
        var now = clock.GetUtcNow();
        foreach (var (k, t) in _tickets) if (t.Until < now) _tickets.TryRemove(k, out _);
        foreach (var (k, s) in _sessions) if (s.LastUsed < now.AddHours(-6) && _sessions.TryRemove(k, out _)) s.Close();
    }
}
