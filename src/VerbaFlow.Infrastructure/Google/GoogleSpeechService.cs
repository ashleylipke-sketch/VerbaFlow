using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using VerbaFlow.Core.Domain;
using VerbaFlow.Core.Providers;
using VerbaFlow.Core.Transcripts;
using VerbaFlow.Infrastructure.Services;

namespace VerbaFlow.Infrastructure.Google;

/// <summary>
/// Google Speech-to-Text v2 on Google's EU servers, for the South African languages Azure does not have (isiXhosa, Sepedi,
/// Setswana, siSwati, Xitsonga, Tshivenda). Google's quick request takes at most a minute of sound, so the recording is
/// turned into 16 kHz mono with ffmpeg and sent in pieces of under a minute, each cut at the quietest moment near its end.
/// Google gives no speaker labels for these languages: everything comes back as one speaker, and meetings get their
/// speakers from the local voice model afterwards. Custom vocabulary is not sent.
/// </summary>
public sealed class GoogleSpeechService(HttpClient http, GoogleSpeechOptions options, string? ffmpegPath) : ISpeechService
{
    public const string EnginePrefix = "google-speech-v2-";
    /// <summary>Google refuses more than 60 s in one quick request; pieces stay under this.</summary>
    public const int MaxPieceMs = 55_000;
    /// <summary>A piece is not cut before this, so it ends at the quietest moment between here and the limit.</summary>
    public const int MinCutMs = 40_000;
    private const int SampleRate = 16000;
    private const double LowConfidenceBelow = 0.6;
    private static readonly TimeSpan[] RetryDelays = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30)];
    /// <summary>Language and model pairs where Google refused word timings or punctuation, so they are not asked for again.</summary>
    private static readonly ConcurrentDictionary<string, bool> PlainOnly = new();

    /// <summary>Google's language code and model for each catalogue code (EU list checked 2026-11-04).</summary>
    public static readonly IReadOnlyDictionary<string, (string Code, string Model)> Languages = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase)
    {
        ["xh-ZA"] = ("xh-ZA", "chirp_3"), ["nso-ZA"] = ("nso-ZA", "chirp_3"),
        ["tn-ZA"] = ("tn-Latn-ZA", "long"), ["ss-ZA"] = ("ss-Latn-ZA", "long"),
        ["ts-ZA"] = ("ts-ZA", "long"), ["ve-ZA"] = ("ve-ZA", "long"),
    };

    /// <summary>Delay between retries. Tests set this to zero.</summary>
    public Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = (t, ct) => Task.Delay(t, ct);

    /// <summary>Turns the audio into 16 kHz mono samples. Tests replace it so ffmpeg is not needed.</summary>
    public Func<Stream, CancellationToken, Task<byte[]>> ToPcm { get; init; } = (audio, ct) => LanguagePerPieceSpeech.ToPcmAsync(ffmpegPath, audio, ct);

    public async Task<TranscriptionResult> TranscribeAsync(Stream audio, TranscribeOptions o, CancellationToken ct)
    {
        if (o.CandidateLanguages.Length != 1 || !Languages.TryGetValue(o.CandidateLanguages[0], out var lang))
            throw new NotSupportedException("Google speech is used for one South African language at a time.");
        var credentials = options.Credentials ?? throw new ProviderException(FaultKind.Settings, "Google speech is not configured.");
        var pcm = await ToPcm(audio, ct);
        var speaker = new Speaker(Guid.NewGuid(), "Speaker 1");
        var segments = new List<Segment>();
        var plain = PlainOnly.ContainsKey(lang.Code + "/" + lang.Model);
        var pieces = Pieces(pcm);
        foreach (var (from, to) in pieces)
        {
            var startMs = (int)(from / 2 * 1000L / SampleRate);
            string json;
            try { json = await RecognizeAsync(credentials, pcm, from, to, lang, plain, ct); }
            catch (ProviderException ex) when (!plain && ex.Message.StartsWith("Google speech said 400:"))
            {
                // Some models do not offer word timings or punctuation for every language: ask once more without them.
                plain = true;
                PlainOnly[lang.Code + "/" + lang.Model] = true;
                json = await RecognizeAsync(credentials, pcm, from, to, lang, plain, ct);
            }
            segments.AddRange(Parse(json, o.CandidateLanguages[0], speaker.Id, startMs, (int)((to - from) / 2 * 1000L / SampleRate)));
        }
        var words = segments.Count(s => s.Words is { Count: > 0 });
        var detail = $"Google {lang.Model}, {pieces.Count} piece{(pieces.Count == 1 ? "" : "s")}"
            + (plain ? ", without word timings or punctuation (not offered for this language)" : words < segments.Count ? ", some passages without word timings" : "")
            + (o.Phrases is { Count: > 0 } ? ", custom vocabulary not used" : "");
        return new TranscriptionResult(EnginePrefix + lang.Model, [speaker], segments, Detail: detail);
    }

    /// <summary>Byte ranges of pieces under a minute, each ending at the quietest tenth of a second between 40 and 55 s.</summary>
    public static List<(long From, long To)> Pieces(byte[] pcm)
    {
        var pieces = new List<(long, long)>();
        long max = (long)MaxPieceMs * SampleRate / 1000 * 2, min = (long)MinCutMs * SampleRate / 1000 * 2, frame = SampleRate / 10 * 2;
        long start = 0;
        while (start < pcm.Length)
        {
            if (pcm.Length - start <= max) { pieces.Add((start, pcm.Length)); break; }
            long best = start + max, quietest = long.MaxValue;
            for (var at = start + min; at + frame <= start + max; at += frame)
            {
                long energy = 0;
                for (var i = at; i < at + frame; i += 2) energy += Math.Abs((int)(short)(pcm[i] | pcm[i + 1] << 8));
                if (energy < quietest) { quietest = energy; best = at + frame / 2 & ~1L; }
            }
            pieces.Add((start, best));
            start = best;
        }
        return pieces;
    }

    private async Task<string> RecognizeAsync(GoogleCredentials credentials, byte[] pcm, long from, long to, (string Code, string Model) lang, bool plain, CancellationToken ct)
    {
        var location = options.Location;
        var url = $"https://{location}-speech.googleapis.com/v2/projects/{options.Project}/locations/{location}/recognizers/_:recognize";
        var config = new JsonObject
        {
            ["explicitDecodingConfig"] = new JsonObject { ["encoding"] = "LINEAR16", ["sampleRateHertz"] = SampleRate, ["audioChannelCount"] = 1 },
            ["languageCodes"] = new JsonArray(lang.Code),
            ["model"] = lang.Model,
        };
        if (!plain) config["features"] = new JsonObject { ["enableWordTimeOffsets"] = true, ["enableAutomaticPunctuation"] = true };
        var body = new JsonObject { ["config"] = config, ["content"] = Convert.ToBase64String(pcm, (int)from, (int)(to - from)) }.ToJsonString();

        for (var attempt = 0; ; attempt++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await credentials.TokenAsync(http, ct));
            // A person's own Google sign-in (gcloud) is billed to the project named here.
            if (credentials.IsUser) req.Headers.Add("x-goog-user-project", options.Project);
            HttpResponseMessage res;
            try { res = await http.SendAsync(req, ct); }
            catch (HttpRequestException ex) when (attempt < RetryDelays.Length) { _ = ex; await Delay(RetryDelays[attempt], ct); continue; }
            using (res)
            {
                var text = await res.Content.ReadAsStringAsync(ct);
                if (res.IsSuccessStatusCode) return text;
                var busy = res.StatusCode is HttpStatusCode.TooManyRequests or >= HttpStatusCode.InternalServerError;
                if (busy && attempt < RetryDelays.Length) { await Delay(RetryDelays[attempt], ct); continue; }
                var kind = res.StatusCode switch
                {
                    HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound => FaultKind.Settings,
                    HttpStatusCode.TooManyRequests => FaultKind.Busy,
                    >= HttpStatusCode.InternalServerError => FaultKind.Unavailable,
                    _ => FaultKind.Other,
                };
                throw new ProviderException(kind, $"Google speech said {(int)res.StatusCode}: {Trim(text)}");
            }
        }
    }

    private static string Trim(string s) => s.Length > 600 ? s[..600] : s;

    /// <summary>One passage per Google result; times moved by <paramref name="offsetMs"/> to the whole recording.</summary>
    public static IEnumerable<Segment> Parse(string json, string language, Guid speakerId, int offsetMs, int pieceMs)
    {
        var root = JsonNode.Parse(json);
        var previousEnd = 0;
        foreach (var r in root?["results"]?.AsArray() ?? [])
        {
            var alt = r?["alternatives"]?.AsArray().FirstOrDefault();
            var endOfResult = Offset(r?["resultEndOffset"]) ?? pieceMs;
            var text = (alt?["transcript"]?.GetValue<string>() ?? "").Trim();
            if (text.Length == 0) { previousEnd = endOfResult; continue; }
            var words = (alt?["words"]?.AsArray() ?? [])
                .Select(w => (Text: w?["word"]?.GetValue<string>() ?? "", Start: Offset(w?["startOffset"]), End: Offset(w?["endOffset"])))
                .Where(w => w.Text.Length > 0)
                .Select(w => new WordTiming(w.Text, (w.Start ?? previousEnd) + offsetMs, Math.Max(w.Start ?? previousEnd, w.End ?? endOfResult) + offsetMs))
                .ToList();
            var start = words.Count > 0 ? words[0].StartMs - offsetMs : previousEnd;
            var end = Math.Max(start, words.Count > 0 ? words[^1].EndMs - offsetMs : endOfResult);
            var confidence = alt?["confidence"]?.GetValue<double>() ?? 0;
            if (confidence <= 0) confidence = 1.0; // these models often send no confidence
            // chirp_3 answers a whole piece (up to 55 s) as one result: split it into sentences where the shown words
            // line up one to one with the timed words, so a passage stays a readable size and speakers can change between them.
            var tokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Count > 0 && tokens.Length == words.Count && end - start > SentenceSplitOverMs)
            {
                var from = 0;
                for (var i = 0; i < tokens.Length; i++)
                {
                    if (i < tokens.Length - 1 && !EndsSentence(tokens[i])) continue;
                    var part = words.GetRange(from, i + 1 - from);
                    yield return new Segment(Guid.NewGuid(), speakerId, language, part[0].StartMs, part[^1].EndMs, string.Join(' ', tokens[from..(i + 1)]),
                        confidence, confidence < LowConfidenceBelow, part);
                    from = i + 1;
                }
            }
            else
                yield return new Segment(Guid.NewGuid(), speakerId, language, start + offsetMs, end + offsetMs, text, confidence,
                    confidence < LowConfidenceBelow, words.Count > 0 ? words : null);
            previousEnd = endOfResult;
        }
    }

    /// <summary>A passage longer than this is split into its sentences.</summary>
    public const int SentenceSplitOverMs = 15_000;

    private static bool EndsSentence(string token) => token.TrimEnd('"', '\'', ')', '”', '’').EndsWith('.') || token.EndsWith('?') || token.EndsWith('!');

    /// <summary>Google writes durations as "1.200s".</summary>
    private static int? Offset(JsonNode? node)
    {
        var s = node?.GetValue<string>();
        if (string.IsNullOrEmpty(s)) return null;
        return double.TryParse(s.TrimEnd('s'), NumberStyles.Float, CultureInfo.InvariantCulture, out var sec) ? (int)Math.Round(sec * 1000) : null;
    }
}

/// <summary>Where Google speech runs and how VerbaFlow signs in to it. Null credentials means it is not set up.</summary>
public sealed record GoogleSpeechOptions(string Project, string Location, GoogleCredentials? Credentials)
{
    public bool IsConfigured => Credentials is not null && !string.IsNullOrWhiteSpace(Project);
}
