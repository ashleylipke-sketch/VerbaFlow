using System.Threading.Channels;
using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;
using VerbaFlow.Core.Providers;
using VerbaFlow.Core.Transcripts;

namespace VerbaFlow.Infrastructure.Azure;

/// <summary>
/// Speech mode: Azure AI Speech real-time recognition fed from the browser's microphone as the person speaks.
/// Set up like the word-for-word dictation route (automatic punctuation only, so spoken commands come back as words,
/// disfluency removal, raw profanity, word timings). With several languages Azure's continuous language
/// identification picks the language phrase by phrase.
/// </summary>
public sealed class AzureLiveSpeech(AzureSpeechOptions options) : ILiveSpeech
{
    public const string EngineName = "azure-ai-speech-live";
    public string Name => EngineName;

    public async Task<ILiveRecognition> StartAsync(LiveOptions o, CancellationToken ct)
    {
        if (o.Languages.Length == 0) throw new NotSupportedException("Live recognition needs at least one language.");
        var locales = o.Languages.Select(AzureSpeechService.ToLocale).Distinct().ToArray();
        var config = SpeechConfig.FromEndpoint(new Uri(options.Endpoint), options.Key);
        config.OutputFormat = OutputFormat.Detailed;
        config.RequestWordLevelTimestamps();
        config.SetProfanity(ProfanityOption.Raw);
        config.SetServiceProperty("punctuation", "implicit", ServicePropertyChannel.UriQueryParameter);
        config.SetProperty(PropertyId.SpeechServiceResponse_PostProcessingOption, "TrueText");
        if (locales.Length == 1) config.SpeechRecognitionLanguage = locales[0];
        else config.SetProperty(PropertyId.SpeechServiceConnection_LanguageIdMode, "Continuous");

        var push = AudioInputStream.CreatePushStream(AudioStreamFormat.GetWaveFormatPCM(16000, 16, 1));
        var audio = AudioConfig.FromStreamInput(push);
        var recogniser = locales.Length == 1 ? new SpeechRecognizer(config, audio)
            : new SpeechRecognizer(config, AutoDetectSourceLanguageConfig.FromLanguages(locales), audio);
        if (o.Phrases is { Count: > 0 })
        {
            var list = PhraseListGrammar.FromRecognizer(recogniser);
            foreach (var p in o.Phrases.Distinct(StringComparer.OrdinalIgnoreCase).Take(500)) list.AddPhrase(p);
        }
        var r = new Recognition(recogniser, push, audio, locales);
        await recogniser.StartContinuousRecognitionAsync().WaitAsync(ct);
        return r;
    }

    private sealed class Recognition : ILiveRecognition
    {
        private readonly SpeechRecognizer _recogniser;
        private readonly PushAudioInputStream _push;
        private readonly AudioConfig _audio;
        private readonly Channel<LiveEvent> _events = Channel.CreateUnbounded<LiveEvent>(new UnboundedChannelOptions { SingleReader = true });
        private readonly Guid _voice = Guid.NewGuid();
        private int _closed;

        public ChannelReader<LiveEvent> Events => _events.Reader;

        public Recognition(SpeechRecognizer recogniser, PushAudioInputStream push, AudioConfig audio, string[] locales)
        {
            _recogniser = recogniser; _push = push; _audio = audio;
            string Language(SpeechRecognitionResult res) =>
                (locales.Length == 1 ? locales[0] : AutoDetectSourceLanguageResult.FromResult(res)?.Language is { Length: > 0 } l ? l : locales[0])
                .Split('-')[0].ToLowerInvariant();
            recogniser.Recognizing += (_, e) =>
            {
                var start = (int)(e.Result.OffsetInTicks / TimeSpan.TicksPerMillisecond);
                _events.Writer.TryWrite(new LiveEvent(LiveEventKind.Partial, e.Result.Text ?? "", start, start + (int)e.Result.Duration.TotalMilliseconds));
            };
            recogniser.Recognized += (_, e) =>
            {
                var res = e.Result;
                if (res.Reason != ResultReason.RecognizedSpeech || string.IsNullOrWhiteSpace(res.Text)) return;
                var best = res.Best()?.FirstOrDefault();
                var start = (int)(res.OffsetInTicks / TimeSpan.TicksPerMillisecond);
                var end = start + (int)res.Duration.TotalMilliseconds;
                var spoken = best?.Words?.Select(w => new WordTiming(w.Word,
                    (int)(w.Offset / TimeSpan.TicksPerMillisecond), (int)((w.Offset + w.Duration) / TimeSpan.TicksPerMillisecond))).ToList();
                var text = res.Text.Trim();
                if (spoken is { Count: > 0 }) text = WordTimingAligner.RestoreSpokenCommands(text, spoken);
                var words = spoken is { Count: > 0 } ? WordTimingAligner.Align(text, spoken, start, end) : null;
                var confidence = best?.Confidence ?? 1.0;
                var phrase = new Segment(Guid.NewGuid(), _voice, Language(res), start, end, text,
                    confidence, confidence < AzureSpeechService.LowConfidenceBelow, words);
                _events.Writer.TryWrite(new LiveEvent(LiveEventKind.Final, text, start, end, phrase));
            };
            recogniser.Canceled += (_, e) =>
            {
                if (e.Reason == CancellationReason.Error)
                    _events.Writer.TryWrite(new LiveEvent(LiveEventKind.Failed, Failure: $"Live recognition failed: {e.ErrorCode}: {e.ErrorDetails}"));
                _events.Writer.TryComplete();
            };
            recogniser.SessionStopped += (_, _) => _events.Writer.TryComplete();
        }

        public void Write(ReadOnlySpan<byte> pcm)
        {
            if (Volatile.Read(ref _closed) == 0 && pcm.Length > 0) _push.Write(pcm.ToArray());
        }

        public async Task CompleteAsync(CancellationToken ct)
        {
            if (Interlocked.Exchange(ref _closed, 1) == 0) _push.Close(); // the end of the audio: the last phrase is finished
            try { await _events.Reader.Completion.WaitAsync(TimeSpan.FromSeconds(30), ct); }
            catch (TimeoutException) { _events.Writer.TryComplete(); }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _closed, 1) == 0) _push.Close();
            try { await _recogniser.StopContinuousRecognitionAsync(); } catch { /* already stopped */ }
            _events.Writer.TryComplete();
            _recogniser.Dispose(); _audio.Dispose(); _push.Dispose();
        }
    }
}
