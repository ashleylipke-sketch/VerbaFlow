using VerbaFlow.Core.Transcripts;

namespace VerbaFlow.Core.Providers;

/// <param name="Phrases">Names and terms the speech service should expect (the company's custom vocabulary).</param>
public sealed record TranscribeOptions(string[] CandidateLanguages, int MaxSpeakers, bool Diarize, IReadOnlyList<string>? Phrases = null);

/// <param name="Warning">Set when the service worked but had to skip something, for example it could not use the vocabulary.</param>
/// <param name="Detail">Plain facts for the history, for example how much of each language was kept when a recording was transcribed once per language.</param>
public sealed record TranscriptionResult(string Engine, IReadOnlyList<Speaker> Speakers, IReadOnlyList<Segment> Segments, string? Warning = null, string? Detail = null);

/// <summary>
/// The speech service heard more than one language in audio sent with several languages and would not pick one
/// (Azure: 422 MultipleLanguagesIdentified). Callers that send short pieces split the piece and try again.
/// </summary>
public sealed class MixedLanguagesException(string message) : InvalidOperationException(message);

/// <summary>Speech to text with diarization and per-segment language detection (Azure AI Speech in production).</summary>
public interface ISpeechService
{
    Task<TranscriptionResult> TranscribeAsync(Stream audio, TranscribeOptions options, CancellationToken ct);
}

/// <summary>
/// Speech to text for a dictation that hands back the words as spoken, so commands such as "comma" and "new paragraph"
/// reach VerbaFlow instead of being acted on (or dropped) by the speech service. One voice, one language.
/// </summary>
public interface IDictationSpeech
{
    string Name { get; }
    Task<TranscriptionResult> TranscribeAsync(Stream audio, TranscribeOptions options, CancellationToken ct);
}

public sealed record AiOutputs(string Engine, string Language, string Summary, IReadOnlyList<string> ActionPoints,
    string Minutes, string ToneOfMeeting);

/// <summary>Summary, minutes, actions and tone (Azure OpenAI in production). Tone must use speech only.</summary>
public interface IAiOutputService
{
    Task<AiOutputs> GenerateAsync(IReadOnlyList<RenderedSegment> transcript, string outputLanguage, CancellationToken ct);
}

public enum ScanOutcome { Clean, Rejected, Unscannable }

public sealed record ScanResult(ScanOutcome Outcome, string? Detail);

/// <summary>Virus scan on upload (Defender for Storage in production). Nothing is stored until it passes.</summary>
public interface IMalwareScanner
{
    Task<ScanResult> ScanAsync(Stream content, CancellationToken ct);
}

/// <summary>
/// Produces the noise-filtered processing copy used only for transcription.
/// The original recording is never altered and stays the record.
/// </summary>
public interface IAudioEnhancer
{
    string Name { get; }
    Task<EnhancedAudio> EnhanceAsync(Stream original, CancellationToken ct);
}

/// <summary>The processing copy. <paramref name="Applied"/> says what was done to it; <paramref name="Warning"/> is set when clean-up was skipped.</summary>
public sealed record EnhancedAudio(Stream Audio, string Applied, string? Warning = null);

/// <summary>One stretch of one voice. <paramref name="Speaker"/> is the diarizer's own number, meaningful only within one recording.</summary>
public sealed record SpeakerTurn(int StartMs, int EndMs, int Speaker);

/// <summary>
/// Works out who spoke when from the audio alone, with no words. Its turns are combined with the speech service's words
/// (see SpeakerAligner) because the speech service's own speaker separation can merge or split voices.
/// </summary>
public interface ISpeakerDiarizer
{
    string Name { get; }
    /// <param name="numSpeakers">The exact number of speakers if it is known, otherwise null to work it out.</param>
    Task<IReadOnlyList<SpeakerTurn>> DiarizeAsync(Stream audio, int? numSpeakers, string? method, CancellationToken ct);
}

public sealed record EmailAttachment(string FileName, string ContentType, byte[] Content);

/// <param name="To">Recipient addresses, already checked.</param>
/// <param name="ReplyTo">The sender's own address, when known, so replies go to a person and not to the no-reply sender.</param>
public sealed record EmailMessage(IReadOnlyList<string> To, string Subject, string PlainText, IReadOnlyList<EmailAttachment> Attachments, string? ReplyTo = null);

/// <summary>Sends email with attachments (Azure Communication Services Email in production).</summary>
public interface IEmailSender
{
    string Name { get; }
    /// <summary>Hands the message to the mail service and returns its reference for the audit trail.</summary>
    Task<string> SendAsync(EmailMessage message, CancellationToken ct);
}

/// <param name="Languages">The locales the person will speak (one, or a few to switch between).</param>
/// <param name="Phrases">Names and terms the speech service should expect.</param>
/// <param name="FromMs">Where in the recording this audio starts (after a lost connection, it is sent again from the last finished phrase).</param>
public sealed record LiveOptions(string[] Languages, IReadOnlyList<string>? Phrases = null, int FromMs = 0);

public enum LiveEventKind { Partial, Final, Failed }

/// <summary>
/// One thing a live recogniser has to say. Times are from the start of the audio written to this recognition.
/// Partial: the words heard so far in the phrase being spoken (they can still change). Final: a finished phrase.
/// Failed: the service stopped working; <paramref name="Failure"/> is technical detail for support only.
/// </summary>
public sealed record LiveEvent(LiveEventKind Kind, string Text = "", int StartMs = 0, int EndMs = 0, Segment? Phrase = null, string? Failure = null);

/// <summary>One live recognition: 16 kHz, 16-bit, mono PCM goes in as it is recorded, words come out as they are heard.</summary>
public interface ILiveRecognition : IAsyncDisposable
{
    /// <summary>What the recogniser hears, in order. Completes when the recognition has finished or failed.</summary>
    System.Threading.Channels.ChannelReader<LiveEvent> Events { get; }
    void Write(ReadOnlySpan<byte> pcm);
    /// <summary>No more audio is coming: the recogniser finishes the last phrase and <see cref="Events"/> completes.</summary>
    Task CompleteAsync(CancellationToken ct);
}

/// <summary>Speech to text while the person speaks (Azure AI Speech real-time recognition in production). One voice.</summary>
public interface ILiveSpeech
{
    string Name { get; }
    Task<ILiveRecognition> StartAsync(LiveOptions options, CancellationToken ct);
}

/// <summary>One passage to check, with a short id the checker gives back (never the passage's own id).</summary>
public sealed record GrammarPassage(string Id, string Text);

/// <summary>The checker's corrected text for one passage, and why, in a few plain words.</summary>
public sealed record GrammarFix(string Id, string Text, string Reason);

/// <summary>
/// Suggests grammar, spelling and punctuation corrections (Azure OpenAI in production). Only suggestions: nothing is
/// changed until the person accepts one. The text is untrusted: it is checked, never followed.
/// </summary>
public interface IGrammarChecker
{
    string Name { get; }
    Task<IReadOnlyList<GrammarFix>> CheckAsync(IReadOnlyList<GrammarPassage> passages, string language, CancellationToken ct);
}
