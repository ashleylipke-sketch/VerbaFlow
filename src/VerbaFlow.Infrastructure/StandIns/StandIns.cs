using System.Security.Cryptography;
using System.Text;
using VerbaFlow.Core.Providers;
using VerbaFlow.Core.Transcripts;

namespace VerbaFlow.Infrastructure.StandIns;

/// <summary>
/// STAND-IN speech service. Produces a deterministic, clearly labelled placeholder transcript so the whole
/// workflow can be exercised without a speech provider. Replace with Azure AI Speech (batch) in production.
/// </summary>
public sealed class StandInSpeechService : ISpeechService
{
    public const string EngineName = "stand-in-speech-v0";

    public async Task<TranscriptionResult> TranscribeAsync(Stream audio, TranscribeOptions options, CancellationToken ct)
    {
        var hash = await SHA256.HashDataAsync(audio, ct);
        var speakerCount = Math.Clamp(2 + hash[0] % 2, 1, Math.Max(1, options.MaxSpeakers));
        var passages = 4 + hash[1] % 5;
        var speakers = Enumerable.Range(1, speakerCount).Select(i => new Speaker(StableGuid(hash, i), $"Speaker {i}")).ToList();
        var langs = options.CandidateLanguages.Length > 0 ? options.CandidateLanguages : ["en"];
        var segments = new List<Segment>();
        for (var i = 0; i < passages; i++)
        {
            var speaker = speakers[i % speakerCount];
            var lang = langs[(i / 2) % langs.Length];
            var text = lang.StartsWith("fr")
                ? $"[Transcription factice] {speaker.Label}, passage {i + 1}."
                : $"[Stand-in transcript] {speaker.Label}, passage {i + 1}.";
            var start = i * 4000;
            var tokens = text.Split(' ');
            var per = 3800 / tokens.Length;
            var words = tokens.Select((t, k) => new WordTiming(t, start + k * per, start + (k + 1) * per)).ToList();
            segments.Add(new Segment(StableGuid(hash, 100 + i), speaker.Id, lang, start, start + 3800, text,
                0.9 - (i % 5 == 4 ? 0.4 : 0), LowConfidence: i % 5 == 4, Words: words));
        }
        return new TranscriptionResult(EngineName, speakers, segments);
    }

    private static Guid StableGuid(byte[] hash, int salt)
    {
        var bytes = SHA256.HashData([.. hash, (byte)salt, (byte)(salt >> 8)]);
        return new Guid(bytes.AsSpan(0, 16));
    }
}

/// <summary>STAND-IN AI outputs. Placeholders only: no real summary, minutes or tone analysis are produced.</summary>
public sealed class StandInAiOutputService : IAiOutputService
{
    public Task<AiOutputs> GenerateAsync(IReadOnlyList<RenderedSegment> transcript, string outputLanguage, CancellationToken ct)
    {
        var speakers = transcript.Select(t => t.SpeakerName).Distinct().Count();
        var languages = string.Join(", ", transcript.Select(t => t.Segment.Language).Distinct());
        return Task.FromResult(new AiOutputs("stand-in-ai-v0", outputLanguage,
            $"[Stand-in summary] {transcript.Count} passages from {speakers} speaker(s) in: {languages}. A real summary needs the AI service connected.",
            ["[Stand-in] No action points are generated until the AI service is connected. Add them manually."],
            "[Stand-in minutes] Not generated until the AI service is connected.",
            "[Stand-in] Tone analysis is not available until the AI service is connected."));
    }
}

/// <summary>STAND-IN malware scanner: rejects the EICAR test string and unscannable (empty) files.</summary>
public sealed class StandInMalwareScanner : IMalwareScanner
{
    private static readonly byte[] Eicar = Encoding.ASCII.GetBytes(@"X5O!P%@AP[4\PZX54(P^)7CC)7}$EICAR-STANDARD-ANTIVIRUS-TEST-FILE!$H+H*");

    public async Task<ScanResult> ScanAsync(Stream content, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        var buffer = new byte[81920];
        long total = 0;
        int n;
        var tail = new List<byte>();
        while ((n = await content.ReadAsync(buffer, ct)) > 0)
        {
            total += n;
            tail.AddRange(buffer.AsSpan(0, n).ToArray());
            if (tail.Count > Eicar.Length * 2 + 81920) tail.RemoveRange(0, tail.Count - (Eicar.Length * 2 + 81920));
            if (tail.ToArray().AsSpan().IndexOf(Eicar) >= 0) return new ScanResult(ScanOutcome.Rejected, "Test virus signature found.");
        }
        return total == 0 ? new ScanResult(ScanOutcome.Unscannable, "The file is empty.") : new ScanResult(ScanOutcome.Clean, null);
    }
}

/// <summary>STAND-IN enhancer: passes audio through unchanged. Production applies noise suppression to the copy only.</summary>
public sealed class StandInAudioEnhancer : IAudioEnhancer
{
    public string Name => "stand-in:none";
    public Task<EnhancedAudio> EnhanceAsync(Stream original, CancellationToken ct) => Task.FromResult(new EnhancedAudio(original, "none"));
}
