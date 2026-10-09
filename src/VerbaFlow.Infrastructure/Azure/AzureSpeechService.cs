using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using VerbaFlow.Core.Domain;
using VerbaFlow.Core.Providers;
using VerbaFlow.Core.Transcripts;

namespace VerbaFlow.Infrastructure.Azure;

/// <summary>Connection settings for an Azure AI Speech resource. The key is a secret: never commit it.</summary>
public sealed record AzureSpeechOptions(string Endpoint, string Key)
{
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Endpoint) && !string.IsNullOrWhiteSpace(Key);
}

/// <summary>
/// Azure AI Speech "fast transcription": one request returns the whole transcript with speakers and language.
/// Diarization needs mono audio, which Azure handles by merging channels unless told otherwise.
/// </summary>
public sealed class AzureSpeechService(HttpClient http, AzureSpeechOptions options) : ISpeechService
{
    public const string EngineName = "azure-ai-speech-fast-transcription-2025-10-15";
    private const string ApiVersion = "2025-10-15";
    private const double LowConfidenceBelow = 0.6;
    /// <summary>When Azure says "too many requests" the quota resets per minute, so wait longer. A Retry-After header, if sent, is obeyed (capped).</summary>
    private static readonly TimeSpan[] BusyDelays = [TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(90)];
    private static readonly TimeSpan MaxRetryAfter = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan[] RetryDelays = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(16)];

    /// <summary>Delay between retries. Tests set this to zero.</summary>
    public Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = (t, ct) => Task.Delay(t, ct);

    public async Task<TranscriptionResult> TranscribeAsync(Stream audio, TranscribeOptions o, CancellationToken ct)
    {
        if (!options.IsConfigured) throw new ProviderException(FaultKind.Settings, "Azure AI Speech is not configured.");
        var seekable = audio.CanSeek ? audio : await BufferAsync(audio, ct);
        var start = seekable.CanSeek ? seekable.Position : 0;
        var url = $"{options.Endpoint.TrimEnd('/')}/speechtotext/transcriptions:transcribe?api-version={ApiVersion}";
        var definition = BuildDefinition(o);
        string? warning = null;

        for (var attempt = 0; ; attempt++)
        {
            seekable.Position = start;
            using var form = new MultipartFormDataContent();
            var file = new KeepOpenContent(seekable);
            file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            form.Add(file, "audio", "audio");
            form.Add(new StringContent(definition), "definition");
            using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = form };
            req.Headers.Add("Ocp-Apim-Subscription-Key", options.Key);

            using var res = await http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);
            if (res.IsSuccessStatusCode)
            {
                var parsed = Parse(await res.Content.ReadAsStringAsync(ct), o);
                return warning is null ? parsed : parsed with { Warning = warning };
            }

            var retriable = res.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.InternalServerError
                or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;
            var delays = res.StatusCode == HttpStatusCode.TooManyRequests ? BusyDelays : RetryDelays;
            if (retriable && attempt < delays.Length)
            {
                var wait = delays[attempt];
                if (res.Headers.RetryAfter?.Delta is { } ra && ra > TimeSpan.Zero) wait = ra < MaxRetryAfter ? ra : MaxRetryAfter;
                await Delay(wait, ct);
                continue;
            }
            // A bad request while a vocabulary was attached: transcribe without it rather than fail the whole recording.
            if (res.StatusCode == HttpStatusCode.BadRequest && warning is null && o.Phrases is { Count: > 0 })
            {
                warning = "Azure did not accept the custom vocabulary, so this recording was transcribed without it.";
                definition = BuildDefinition(o with { Phrases = null });
                continue;
            }
            throw new ProviderException(Services.FailureReporter.ForStatus(res.StatusCode), Describe(res.StatusCode, await res.Content.ReadAsStringAsync(ct)));
        }
    }

    public static string BuildDefinition(TranscribeOptions o)
    {
        var def = new JsonObject();
        var locales = o.CandidateLanguages.Select(ToLocale).Distinct().ToArray();
        if (locales.Length > 0) def["locales"] = new JsonArray(locales.Select(l => (JsonNode)l).ToArray());
        if (o.Diarize) def["diarization"] = new JsonObject { ["enabled"] = true, ["maxSpeakers"] = Math.Clamp(o.MaxSpeakers, 2, 35) };
        if (o.Phrases is { Count: > 0 })
            def["phraseList"] = new JsonObject { ["phrases"] = new JsonArray(o.Phrases.Distinct(StringComparer.OrdinalIgnoreCase).Take(2000).Select(x => (JsonNode)x).ToArray()) };
        def["profanityFilterMode"] = "None"; // a transcript must record what was said
        return def.ToJsonString();
    }

    /// <summary>"en" becomes en-GB, "fr" fr-FR and so on. Full locales such as en-US pass through.</summary>
    public static string ToLocale(string lang) => lang.Contains('-') ? lang : lang.ToLowerInvariant() switch
    {
        "en" => "en-GB", "fr" => "fr-FR", "es" => "es-ES", "de" => "de-DE", "it" => "it-IT", "pt" => "pt-PT", "nl" => "nl-NL",
        var other => other,
    };

    public static TranscriptionResult Parse(string json, TranscribeOptions o)
    {
        using var doc = JsonDocument.Parse(json);
        var speakers = new Dictionary<int, Speaker>();
        var segments = new List<Segment>();
        if (doc.RootElement.TryGetProperty("phrases", out var phrases))
        {
            foreach (var p in phrases.EnumerateArray())
            {
                var text = p.TryGetProperty("text", out var t) ? t.GetString()?.Trim() ?? "" : "";
                if (text.Length == 0) continue;
                var no = p.TryGetProperty("speaker", out var s) && s.TryGetInt32(out var n) ? n : 1;
                if (!speakers.TryGetValue(no, out var speaker))
                    speakers[no] = speaker = new Speaker(Guid.NewGuid(), $"Speaker {speakers.Count + 1}");
                var start = p.TryGetProperty("offsetMilliseconds", out var off) ? off.GetInt32() : 0;
                var len = p.TryGetProperty("durationMilliseconds", out var dur) ? dur.GetInt32() : 0;
                var locale = p.TryGetProperty("locale", out var loc) ? loc.GetString() : null;
                var conf = p.TryGetProperty("confidence", out var c) && c.TryGetDouble(out var cd) ? cd : 1.0;
                var words = p.TryGetProperty("words", out var w) && w.ValueKind == JsonValueKind.Array
                    ? w.EnumerateArray().Select(x => new WordTiming(x.GetProperty("text").GetString() ?? "",
                        x.GetProperty("offsetMilliseconds").GetInt32(),
                        x.GetProperty("offsetMilliseconds").GetInt32() + x.GetProperty("durationMilliseconds").GetInt32()))
                        .Where(x => x.Text.Length > 0).ToList()
                    : null;
                segments.Add(new Segment(Guid.NewGuid(), speaker.Id, ShortLanguage(locale, o), start, start + len, text, conf,
                    conf < LowConfidenceBelow, words is { Count: > 0 } ? words : null));
            }
        }
        return new TranscriptionResult(EngineName, speakers.Values.ToList(), MergeTurns(segments));
    }

    /// <summary>A new paragraph starts after this much silence from the same speaker.</summary>
    public const int MaxGapMs = 4000;
    /// <summary>...or when a paragraph would grow past this many characters, so long monologues stay editable.</summary>
    public const int MaxParagraphChars = 800;

    /// <summary>
    /// Azure returns one entry per phrase. Joins consecutive phrases from the same speaker, in the same language and
    /// without a long pause, into one paragraph. A paragraph is low confidence if any phrase in it was.
    /// </summary>
    public static IReadOnlyList<Segment> MergeTurns(IEnumerable<Segment> phrases)
    {
        var result = new List<Segment>();
        foreach (var p in phrases.OrderBy(x => x.StartMs))
        {
            var last = result.Count > 0 ? result[^1] : null;
            if (last is not null && last.SpeakerId == p.SpeakerId && last.Language == p.Language
                && p.StartMs - last.EndMs <= MaxGapMs && last.Text.Length + 1 + p.Text.Length <= MaxParagraphChars)
            {
                result[^1] = last with
                {
                    EndMs = Math.Max(last.EndMs, p.EndMs),
                    Text = last.Text + " " + p.Text,
                    Confidence = Math.Min(last.Confidence, p.Confidence),
                    LowConfidence = last.LowConfidence || p.LowConfidence,
                    Words = last.Words is not null && p.Words is not null ? [.. last.Words, .. p.Words] : null,
                };
            }
            else result.Add(p);
        }
        return result;
    }

    private static string ShortLanguage(string? locale, TranscribeOptions o) =>
        string.IsNullOrWhiteSpace(locale) ? (o.CandidateLanguages.FirstOrDefault() ?? "en") : locale.Split('-')[0].ToLowerInvariant();

    private static string Describe(HttpStatusCode code, string body)
    {
        string? detail = null;
        try { detail = JsonDocument.Parse(body).RootElement.GetProperty("error").GetProperty("message").GetString(); } catch { /* not JSON */ }
        var hint = code switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => " Check the Speech key and endpoint.",
            HttpStatusCode.NotFound => " Check the Speech endpoint address.",
            HttpStatusCode.TooManyRequests => " Azure is limiting how many requests this Speech resource accepts per minute (the free F0 tier allows very few). Move the resource to Standard S0, or retry later.",
            HttpStatusCode.RequestEntityTooLarge => " The file is too large for transcription (limit 500 MB or 5 hours).",
            _ => "",
        };
        return $"Azure AI Speech returned {(int)code} {code}.{hint}{(detail is null ? "" : " " + detail)}";
    }

    private static async Task<Stream> BufferAsync(Stream s, CancellationToken ct)
    {
        var ms = new MemoryStream();
        await s.CopyToAsync(ms, ct);
        ms.Position = 0;
        return ms;
    }

    /// <summary>Sends a stream without closing it afterwards, so a retry can send the same file again.</summary>
    private sealed class KeepOpenContent(Stream stream) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream target, TransportContext? context) => stream.CopyToAsync(target);
        protected override Task SerializeToStreamAsync(Stream target, TransportContext? context, CancellationToken ct) => stream.CopyToAsync(target, ct);
        protected override bool TryComputeLength(out long length)
        {
            length = stream.Length - stream.Position;
            return true;
        }
    }
}
