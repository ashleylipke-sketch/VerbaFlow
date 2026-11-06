using VerbaFlow.Core.Transcripts;

namespace VerbaFlow.Core.Providers;

/// <param name="Phrases">Names and terms the speech service should expect (the company's custom vocabulary).</param>
/// <param name="Enhanced">Use the speech service's newer AI model (Azure "LLM Speech", enhanced mode) instead of the standard one.</param>
public sealed record TranscribeOptions(string[] CandidateLanguages, int MaxSpeakers, bool Diarize, IReadOnlyList<string>? Phrases = null, bool Enhanced = false);

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
    Task<EnhancedAudio> EnhanceAsync(Stream original, CancellationToken ct) => EnhanceAsync(original, NoiseReduction.None, ct);
    /// <summary>As above, also taking away background noise (hum, fans, traffic, chatter) when asked.</summary>
    Task<EnhancedAudio> EnhanceAsync(Stream original, NoiseReduction noise, CancellationToken ct);
}

/// <summary>How much background noise the processing copy has taken away. Over-filtering can cost quiet words, so it is a choice.</summary>
public enum NoiseReduction { None, Light, Strong }

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

/// <summary>
/// Voice fingerprints for chosen stretches of a recording, to compare a stretch with a known person's voice sample.
/// One result per stretch, in order; null where the stretch was too short or unclear to fingerprint.
/// </summary>
public interface IVoicePrints
{
    Task<IReadOnlyList<float[]?>> PrintsAsync(Stream audio, IReadOnlyList<(int StartMs, int EndMs)> stretches, CancellationToken ct);
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

/// <param name="WebLink">Where the draft opens in Outlook on the web, when the mail service gives one.</param>
/// <param name="Reference">The mail service's id for the draft, for the audit trail.</param>
public sealed record MailDraft(string? WebLink, string Reference);

/// <summary>The person has no mailbox the mail service can reach (for example a Microsoft account with no Exchange Online mailbox).</summary>
public sealed class NoMailboxException(string message) : Exception(message);

/// <summary>
/// Puts a new, unsent email with attachments in the signed-in person's own mailbox, as a draft (Microsoft Graph in
/// production, acting only as that person). They choose who it goes to in their own email program and send it there.
/// </summary>
public interface IMailDrafts
{
    string Name { get; }
    /// <summary>Whether this person can use it. <paramref name="userToken"/> is their sign-in token, when they signed in with Microsoft.</summary>
    bool CanUse(VerbaFlow.Core.Domain.User user, string? userToken);
    Task<MailDraft> CreateAsync(VerbaFlow.Core.Domain.User user, string? userToken, EmailMessage message, CancellationToken ct);
}

/// <summary>
/// Puts a draft in the person's own mailbox with a mail permission their own browser got for it: a Microsoft Graph token
/// for a personal Microsoft account (Outlook.com, Hotmail), or a Google token for Gmail. VerbaFlow keeps no copy of it.
/// </summary>
public interface IOwnMailDrafts
{
    /// <summary>Whether Gmail drafts can be offered (Google sign-in is set up, so the browser can ask Google for permission).</summary>
    bool Gmail { get; }
    /// <param name="service"><see cref="MailServices.Outlook"/> or <see cref="MailServices.Gmail"/>.</param>
    Task<MailDraft> CreateAsync(string service, string mailToken, EmailMessage message, CancellationToken ct);
}

public static class MailServices
{
    public const string Outlook = "outlook";
    public const string Gmail = "gmail";
}

/// <summary>One piece of text to translate, with a short id the translator gives back.</summary>
public sealed record TextPiece(string Id, string Text);

/// <summary>
/// Translates pieces of a recording's text into another language (Azure OpenAI in production). The text is untrusted:
/// it is translated, never followed. Pieces the translator leaves out are simply missing from the answer.
/// </summary>
public interface ITranslator
{
    string Name { get; }
    /// <param name="language">The language to translate into, as a code such as "fr" or "zh".</param>
    Task<IReadOnlyList<TextPiece>> TranslateAsync(IReadOnlyList<TextPiece> pieces, string language, CancellationToken ct);
}

/// <param name="Announcement">Posted in the meeting chat when the note-taker joins, so everyone knows it is recording.</param>
/// <param name="Reference">VerbaFlow's own id, kept with the note-taker so it can be found again.</param>
public sealed record NoteTakerRequest(string MeetingUrl, string DisplayName, DateTimeOffset? JoinAt, string Announcement, Guid Reference);

/// <summary>Unknown: the service reported something new (for example a breakout room); the booking keeps its current state.</summary>
public enum NoteTakerPhase { Scheduled, Joining, WaitingToBeLetIn, Recording, MeetingEnded, Ready, Failed, Unknown }

/// <summary>
/// What the note-taker service says now. <paramref name="Detail"/> is technical (for support only).
/// When Ready, <paramref name="VideoUrl"/> is where the video can be downloaded and <paramref name="SpeakersUrl"/>
/// (if any) where the platform's record of who spoke when can be.
/// </summary>
/// <paramref name="RecordingId"/> is the service's id for the recording, used to ask for each person's own audio.
public sealed record NoteTakerStatus(NoteTakerPhase Phase, string? Detail = null, string? VideoUrl = null, string? SpeakersUrl = null, string? RecordingId = null);

/// <summary>Where one stretch of one participant's own audio can be downloaded, and where it starts in the meeting.</summary>
public sealed record PersonAudioDownload(int Participant, string Name, int StartMs, int DurationMs, string Url, string Format);

/// <summary>
/// Each participant's own audio. <paramref name="Ready"/> is false while the service is still preparing it; an empty,
/// ready list means there is none (not asked for, or the platform does not give it). <paramref name="Detail"/> is technical.
/// </summary>
public sealed record PersonAudio(bool Ready, IReadOnlyList<PersonAudioDownload> Parts, string? Detail = null);

/// <summary>
/// A note-taker that joins an online meeting as a visible participant and records it (Recall.ai in production, which
/// joins Zoom, Google Meet, Microsoft Teams and Webex).
/// </summary>
public interface INoteTakerService
{
    string Name { get; }
    /// <summary>Books a note-taker for the meeting and returns the service's id for it.</summary>
    Task<string> ScheduleAsync(NoteTakerRequest request, CancellationToken ct);
    Task<NoteTakerStatus> GetAsync(string botId, CancellationToken ct);
    /// <summary>Calls the note-taker off: it does not join, or leaves if it is already in the meeting.</summary>
    Task CancelAsync(string botId, CancellationToken ct);
    /// <summary>The meeting's video (MP4).</summary>
    Task<Stream> DownloadVideoAsync(string url, CancellationToken ct);
    /// <summary>Who spoke when, as the meeting platform reported it.</summary>
    Task<IReadOnlyList<VerbaFlow.Core.Domain.PlatformSpeech>> DownloadSpeakersAsync(string url, CancellationToken ct);
    /// <summary>Each participant's own audio of the recording, if the service made it.</summary>
    Task<PersonAudio> ListPersonAudioAsync(string botId, NoteTakerStatus status, CancellationToken ct);
    /// <summary>One stretch of one participant's own audio (from <see cref="ListPersonAudioAsync"/>).</summary>
    Task<Stream> DownloadPersonAudioAsync(PersonAudioDownload part, CancellationToken ct);
    /// <summary>Removes the recording from the note-taker service once VerbaFlow has its own copy.</summary>
    Task ForgetAsync(string botId, CancellationToken ct);
}

/// <summary>
/// Reads the meetings in a person's calendar between two times (Microsoft 365 through Microsoft Graph, a private iCal
/// address, or the sample stand-in). Failures are ProviderExceptions with technical text, for support only.
/// </summary>
public interface ICalendarReader
{
    /// <summary>The calendar source this reader handles (see CalendarSources).</summary>
    string Source { get; }
    Task<IReadOnlyList<VerbaFlow.Core.Domain.CalendarMeeting>> ReadAsync(VerbaFlow.Core.Domain.CalendarSettings settings,
        VerbaFlow.Core.Domain.User user, DateTimeOffset from, DateTimeOffset to, CancellationToken ct);
}
