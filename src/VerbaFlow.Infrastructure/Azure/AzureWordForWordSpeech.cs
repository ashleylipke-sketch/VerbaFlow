using System.Diagnostics;
using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;
using VerbaFlow.Core.Domain;
using VerbaFlow.Core.Providers;
using VerbaFlow.Core.Transcripts;

namespace VerbaFlow.Infrastructure.Azure;

/// <summary>
/// Dictations through Azure AI Speech's real-time recogniser (the Speech SDK) instead of fast transcription. Fast
/// transcription acts on some spoken punctuation itself and was seen to drop "new paragraph" without a trace
/// (owner's dictation, 2026-10-11). Here punctuation is set to automatic only, so spoken commands come back as words
/// and <see cref="DictationCommands"/> applies them. It reads the audio at about real-time speed, so it is slower.
/// Needs ffmpeg to turn the recording into the 16 kHz mono WAV the recogniser reads.
/// </summary>
public sealed class AzureWordForWordSpeech(AzureSpeechOptions options, string? ffmpegPath) : IDictationSpeech
{
    public const string EngineName = "azure-ai-speech-word-for-word";
    public string Name => EngineName;

    public async Task<TranscriptionResult> TranscribeAsync(Stream audio, TranscribeOptions o, CancellationToken ct)
    {
        if (o.CandidateLanguages.Length != 1) throw new NotSupportedException("Word-for-word transcription needs exactly one language.");
        var locale = AzureSpeechService.ToLocale(o.CandidateLanguages[0]);
        var dir = Directory.CreateTempSubdirectory("vf-wfw-");
        try
        {
            var raw = Path.Combine(dir.FullName, "in.bin");
            var wav = Path.Combine(dir.FullName, "audio.wav");
            if (audio.CanSeek) audio.Position = 0;
            await using (var f = File.Create(raw)) await audio.CopyToAsync(f, ct);
            await ToWavAsync(raw, wav, ct);

            var config = SpeechConfig.FromEndpoint(new Uri(options.Endpoint), options.Key);
            config.SpeechRecognitionLanguage = locale;
            config.OutputFormat = OutputFormat.Detailed;
            config.RequestWordLevelTimestamps();
            config.SetProfanity(ProfanityOption.Raw); // a transcript must record what was said
            // Automatic punctuation only: spoken punctuation stays as words for VerbaFlow to apply.
            config.SetServiceProperty("punctuation", "implicit", ServicePropertyChannel.UriQueryParameter);
            // Leave out stumbles and repeats ("I will quite... I will quite clearly"), as fast transcription does.
            config.SetProperty(PropertyId.SpeechServiceResponse_PostProcessingOption, "TrueText");

            using var input = AudioConfig.FromWavFileInput(wav);
            using var recogniser = new SpeechRecognizer(config, input);
            if (o.Phrases is { Count: > 0 })
            {
                var list = PhraseListGrammar.FromRecognizer(recogniser);
                foreach (var p in o.Phrases.Distinct(StringComparer.OrdinalIgnoreCase).Take(500)) list.AddPhrase(p);
            }

            var voice = new Speaker(Guid.NewGuid(), "Speaker 1");
            var language = locale.Split('-')[0].ToLowerInvariant();
            var phrases = new List<Segment>();
            string? failure = null;
            var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            recogniser.Recognized += (_, e) =>
            {
                var r = e.Result;
                if (r.Reason != ResultReason.RecognizedSpeech || string.IsNullOrWhiteSpace(r.Text)) return;
                var best = r.Best()?.FirstOrDefault();
                var start = (int)(r.OffsetInTicks / TimeSpan.TicksPerMillisecond);
                var end = start + (int)r.Duration.TotalMilliseconds;
                var spoken = best?.Words?.Select(w => new WordTiming(w.Word,
                    (int)(w.Offset / TimeSpan.TicksPerMillisecond), (int)((w.Offset + w.Duration) / TimeSpan.TicksPerMillisecond))).ToList();
                // The timed words are the words as spoken; give each shown word a time so following along stays in step.
                var text = r.Text.Trim();
                var words = spoken is { Count: > 0 } ? WordTimingAligner.Align(text, spoken, start, end) : null;
                var confidence = best?.Confidence ?? 1.0;
                lock (phrases)
                    phrases.Add(new Segment(Guid.NewGuid(), voice.Id, language, start, end, text,
                        confidence, confidence < AzureSpeechService.LowConfidenceBelow, words));
            };
            recogniser.Canceled += (_, e) =>
            {
                if (e.Reason == CancellationReason.Error) failure = $"{e.ErrorCode}: {e.ErrorDetails}";
                finished.TrySetResult();
            };
            recogniser.SessionStopped += (_, _) => finished.TrySetResult();

            await recogniser.StartContinuousRecognitionAsync();
            try { await finished.Task.WaitAsync(TimeSpan.FromMinutes(30), ct); }
            finally { await recogniser.StopContinuousRecognitionAsync(); }
            if (failure is not null)
                throw new ProviderException(failure.Contains("AuthenticationFailure") || failure.Contains("Forbidden") ? FaultKind.Settings
                    : failure.Contains("TooManyRequests") ? FaultKind.Busy : FaultKind.Unavailable, "Word-for-word transcription failed: " + failure);

            List<Segment> ordered;
            lock (phrases) ordered = phrases.OrderBy(p => p.StartMs).ToList();
            return new TranscriptionResult(EngineName, [voice], AzureSpeechService.MergeTurns(ordered));
        }
        finally { try { dir.Delete(true); } catch { /* best effort */ } }
    }

    private async Task ToWavAsync(string input, string output, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(string.IsNullOrWhiteSpace(ffmpegPath) ? "ffmpeg" : ffmpegPath)
            { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "-hide_banner", "-loglevel", "error", "-y", "-i", input, "-ac", "1", "-ar", "16000", "-sample_fmt", "s16", "-f", "wav", output })
            psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg did not start.");
        var err = proc.StandardError.ReadToEndAsync(ct);
        _ = proc.StandardOutput.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);
        if (proc.ExitCode != 0 || !File.Exists(output)) throw new InvalidOperationException("ffmpeg could not convert the dictation: " + await err);
    }
}
