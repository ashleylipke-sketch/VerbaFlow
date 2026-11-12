using VerbaFlow.Core.Domain;
using VerbaFlow.Core.Providers;

namespace VerbaFlow.Infrastructure.Services;

/// <summary>
/// Sends a recording to the speech service that has its language: the South African languages Azure does not have
/// (isiXhosa, Sepedi, Setswana, siSwati, Xitsonga, Tshivenda) go to Google, everything else to Azure as before.
/// A Google language is always the only language of its recording (checked when the recording is made).
/// </summary>
public sealed class SpeechRouter(ISpeechService azure, ISpeechService? google) : ISpeechService
{
    public Task<TranscriptionResult> TranscribeAsync(Stream audio, TranscribeOptions options, CancellationToken ct)
    {
        var langs = options.CandidateLanguages;
        if (!langs.Any(SpokenLanguageCatalogue.IsGoogle)) return azure.TranscribeAsync(audio, options, ct);
        SpokenLanguageCatalogue.CheckCombination(langs);
        if (google is null) throw new ProviderException(FaultKind.Settings, $"Google speech is not set up, so {langs[0]} cannot be transcribed.");
        return google.TranscribeAsync(audio, options with { Diarize = false }, ct);
    }
}

/// <summary>Whether the languages that go to Google can be chosen on this installation.</summary>
public sealed record SpeechLanguages(bool GoogleOn);
