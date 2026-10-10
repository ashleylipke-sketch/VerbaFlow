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
        var locales = o.CandidateLanguages.Select(ToLocale).Distinct().ToArray();
        if (locales.Length < 2)
        {
            var (json, warning) = await SendAsync(seekable, start, o, ct);
            var parsed = Parse(json, o);
            return warning is null ? parsed : parsed with { Warning = warning };
        }

        // Given several languages, Azure picks one for the whole recording. So transcribe once per language and keep,
        // for each stretch of speech, the version Azure was most confident about.
        var runs = new List<IReadOnlyList<Segment>>();
        var speakers = new List<Speaker>();
        string? firstWarning = null;
        foreach (var locale in locales)
        {
            var single = o with { CandidateLanguages = [locale] };
            var (json, warning) = await SendAsync(seekable, start, single, ct);
            firstWarning ??= warning;
            var (runSpeakers, phrases) = ParsePhrases(json, single, speakers);
            speakers = runSpeakers;
            runs.Add(phrases);
        }
        var (chosen, kept) = CombineLanguages(runs);
        var used = chosen.Select(x => x.SpeakerId).ToHashSet();
        var detail = string.Join(", ", locales.Select((l, i) => $"{l}: {kept[i]} kept of {runs[i].Count}"));
        return new TranscriptionResult(EngineName + "+per-language", speakers.Where(x => used.Contains(x.Id)).ToList(), MergeTurns(chosen),
            firstWarning, detail);
    }

    /// <summary>Sends the audio once, retrying when Azure is busy, and without the vocabulary if Azure refuses it.</summary>
    private async Task<(string Json, string? Warning)> SendAsync(Stream seekable, long start, TranscribeOptions o, CancellationToken ct)
    {
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
            if (res.IsSuccessStatusCode) return (await res.Content.ReadAsStringAsync(ct), warning);

            var retriable = res.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout or HttpStatusCode.InternalServerError
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
            var body = await res.Content.ReadAsStringAsync(ct);
            throw new ProviderException(Services.FailureReporter.ForStatus(res.StatusCode), Describe(res.StatusCode, body) + Diagnostics(res, body, attempt + 1));
        }
    }

    /// <summary>
    /// Combines one transcription per language. Phrases from different runs that overlap in time cover the same stretch of
    /// speech; for each stretch, the run with the highest confidence (weighted by phrase length) wins. A stretch only one run
    /// heard is kept as it is, so speech one language model dropped can come back from the other. Ties go to the first language.
    /// Returns the chosen phrases and how many phrases each run contributed.
    /// </summary>
    public static (List<Segment> Chosen, int[] Kept) CombineLanguages(IReadOnlyList<IReadOnlyList<Segment>> runs)
    {
        var all = runs.SelectMany((r, i) => r.Select(p => (Run: i, P: p))).OrderBy(x => x.P.StartMs).ThenBy(x => x.Run).ToList();
        var chosen = new List<Segment>();
        var kept = new int[runs.Count];
        var i0 = 0;
        while (i0 < all.Count)
        {
            // A stretch: phrases that overlap one another, directly or through a chain.
            var end = all[i0].P.EndMs;
            var j = i0 + 1;
            while (j < all.Count && all[j].P.StartMs < end) { end = Math.Max(end, all[j].P.EndMs); j++; }
            var stretch = all.GetRange(i0, j - i0);
            var best = stretch.GroupBy(x => x.Run)
                .Select(g => (Run: g.Key, Score: g.Sum(x => x.P.Confidence * Math.Max(1, x.P.EndMs - x.P.StartMs)) / g.Sum(x => Math.Max(1, x.P.EndMs - x.P.StartMs))))
                .OrderByDescending(x => x.Score).ThenBy(x => x.Run).First().Run;
            foreach (var x in stretch.Where(x => x.Run == best)) { chosen.Add(x.P); kept[best]++; }
            i0 = j;
        }
        return (chosen, kept);
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
        var (speakers, phrases) = ParsePhrases(json, o, []);
        return new TranscriptionResult(EngineName, speakers, MergeTurns(phrases));
    }

    /// <summary>
    /// Reads Azure's phrases. Speaker numbers are Azure's own for this request; <paramref name="known"/> carries speakers
    /// from an earlier request on the same recording, so "Speaker 1" means the same label across runs (Azure's numbering
    /// usually, though not always, follows who speaks first).
    /// </summary>
    public static (List<Speaker> Speakers, List<Segment> Phrases) ParsePhrases(string json, TranscribeOptions o, IReadOnlyList<Speaker> known)
    {
        using var doc = JsonDocument.Parse(json);
        var all = known.ToList();
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
                {
                    var label = $"Speaker {speakers.Count + 1}";
                    speaker = all.FirstOrDefault(x => x.Label == label);
                    if (speaker is null) all.Add(speaker = new Speaker(Guid.NewGuid(), label));
                    speakers[no] = speaker;
                }
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
        return (known.Count > 0 ? all : speakers.Values.ToList(), segments);
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

    /// <summary>Support-only extra detail: how many tries were made, Azure's own headers and the start of its reply. Never contains our key.</summary>
    private static string Diagnostics(HttpResponseMessage res, string body, int tries)
    {
        var headers = string.Join("; ", res.Headers.Where(h => !h.Key.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase))
            .Select(h => $"{h.Key}={string.Join(",", h.Value)}"));
        var text = body.Length > 600 ? body[..600] : body;
        return $" [tries={tries}] [headers: {headers}] [body: {(text.Length == 0 ? "(empty)" : text)}]";
    }

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
