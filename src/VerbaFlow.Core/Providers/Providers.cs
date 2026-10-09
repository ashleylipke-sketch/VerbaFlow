using VerbaFlow.Core.Transcripts;

namespace VerbaFlow.Core.Providers;

/// <param name="Phrases">Names and terms the speech service should expect (the company's custom vocabulary).</param>
public sealed record TranscribeOptions(string[] CandidateLanguages, int MaxSpeakers, bool Diarize, IReadOnlyList<string>? Phrases = null);

/// <param name="Warning">Set when the service worked but had to skip something, for example it could not use the vocabulary.</param>
public sealed record TranscriptionResult(string Engine, IReadOnlyList<Speaker> Speakers, IReadOnlyList<Segment> Segments, string? Warning = null);

/// <summary>Speech to text with diarization and per-segment language detection (Azure AI Speech in production).</summary>
public interface ISpeechService
{
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
    Task<IReadOnlyList<SpeakerTurn>> DiarizeAsync(Stream audio, int? numSpeakers, CancellationToken ct);
}
