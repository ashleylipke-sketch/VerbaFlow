using System.Net;
using System.Text;
using System.Text.Json;
using VerbaFlow.Core.Domain;
using VerbaFlow.Core.Providers;
using VerbaFlow.Infrastructure.Azure;

namespace VerbaFlow.Tests.Services;

public class AzureSpeechTests
{
    private sealed class Handler(Func<HttpRequestMessage, string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Req, string Body)> Calls { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            var body = r.Content is null ? "" : await r.Content.ReadAsStringAsync(ct);
            Calls.Add((r, body));
            return respond(r, body);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode code, string json) =>
        new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private const string Good = """
    { "durationMilliseconds": 9000, "phrases": [
      { "speaker": 1, "offsetMilliseconds": 500, "durationMilliseconds": 2000, "text": "Good morning everyone.", "locale": "en-GB", "confidence": 0.95 },
      { "speaker": 2, "offsetMilliseconds": 3000, "durationMilliseconds": 2500, "text": "Bonjour à tous.", "locale": "fr-FR", "confidence": 0.9 },
      { "speaker": 1, "offsetMilliseconds": 6000, "durationMilliseconds": 1000, "text": "mumble", "locale": "en-GB", "confidence": 0.3 },
      { "speaker": 2, "offsetMilliseconds": 7500, "durationMilliseconds": 500, "text": "  ", "locale": "en-GB", "confidence": 0.9 } ] }
    """;

    private static AzureSpeechService Make(Handler h) =>
        new(new HttpClient(h), new AzureSpeechOptions("https://demo.cognitiveservices.azure.com/", "secret-key-123"))
        { Delay = (_, _) => Task.CompletedTask };

    private static readonly TranscribeOptions Opts = new(["en"], 8, true);

    [Fact]
    public async Task Maps_phrases_to_speakers_languages_and_low_confidence()
    {
        var h = new Handler((_, _) => Json(HttpStatusCode.OK, Good));
        var r = await Make(h).TranscribeAsync(new MemoryStream([1, 2, 3]), Opts, default);
        Assert.Equal(2, r.Speakers.Count);
        Assert.Equal(["Speaker 1", "Speaker 2"], r.Speakers.Select(s => s.Label));
        Assert.Equal(3, r.Segments.Count); // the blank phrase is dropped
        Assert.Equal(["en", "fr", "en"], r.Segments.Select(s => s.Language));
        Assert.Equal(r.Segments[0].SpeakerId, r.Segments[2].SpeakerId);
        Assert.NotEqual(r.Segments[0].SpeakerId, r.Segments[1].SpeakerId);
        Assert.Equal((500, 2500), (r.Segments[0].StartMs, r.Segments[0].EndMs));
        Assert.Equal([false, false, true], r.Segments.Select(s => s.LowConfidence));
        Assert.Equal("Bonjour à tous.", r.Segments[1].Text);
    }

    [Fact]
    public async Task Sends_key_header_locales_and_diarization_but_never_puts_the_key_in_the_url_or_body()
    {
        var h = new Handler((_, _) => Json(HttpStatusCode.OK, Good));
        await Make(h).TranscribeAsync(new MemoryStream([1, 2, 3]), Opts, default);
        var (req, body) = Assert.Single(h.Calls);
        Assert.Equal("https://demo.cognitiveservices.azure.com/speechtotext/transcriptions:transcribe?api-version=2025-10-15", req.RequestUri!.ToString());
        Assert.Equal("secret-key-123", req.Headers.GetValues("Ocp-Apim-Subscription-Key").Single());
        Assert.DoesNotContain("secret-key-123", req.RequestUri.ToString());
        Assert.DoesNotContain("secret-key-123", body);
        Assert.Contains("\"locales\":[\"en-GB\"]", body);
        Assert.Contains("\"diarization\":{\"enabled\":true,\"maxSpeakers\":8}", body);
        Assert.Contains("\"profanityFilterMode\":\"None\"", body);
    }

    [Fact]
    public async Task Retries_throttling_and_resends_the_whole_file()
    {
        var n = 0;
        var h = new Handler((_, _) => ++n < 3 ? Json(HttpStatusCode.TooManyRequests, "{}") : Json(HttpStatusCode.OK, Good));
        var r = await Make(h).TranscribeAsync(new MemoryStream(Encoding.ASCII.GetBytes("AUDIOBYTES")), Opts, default);
        Assert.Equal(3, h.Calls.Count);
        Assert.All(h.Calls, c => Assert.Contains("AUDIOBYTES", c.Body));
        Assert.Equal(3, r.Segments.Count);
    }

    [Fact]
    public async Task Gives_up_after_repeated_server_errors()
    {
        var h = new Handler((_, _) => Json(HttpStatusCode.ServiceUnavailable, "{}"));
        var ex = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => Make(h).TranscribeAsync(new MemoryStream([1]), Opts, default));
        Assert.Equal(5, h.Calls.Count);
        Assert.Contains("503", ex.Message);
    }

    [Fact]
    public async Task A_rejected_key_fails_at_once_with_a_clear_message_that_does_not_repeat_the_key()
    {
        var h = new Handler((_, _) => Json(HttpStatusCode.Unauthorized, """{"error":{"message":"Access denied due to invalid subscription key."}}"""));
        var ex = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => Make(h).TranscribeAsync(new MemoryStream([1]), Opts, default));
        Assert.Single(h.Calls);
        Assert.Contains("Check the Speech key and endpoint", ex.Message);
        Assert.Contains("invalid subscription key", ex.Message);
        Assert.DoesNotContain("secret-key-123", ex.Message);
    }

    [Fact]
    public async Task Works_with_a_non_seekable_stream()
    {
        var h = new Handler((_, _) => Json(HttpStatusCode.OK, Good));
        await Make(h).TranscribeAsync(new ForwardOnly(new MemoryStream(Encoding.ASCII.GetBytes("XYZ"))), Opts, default);
        Assert.Contains("XYZ", h.Calls[0].Body);
    }

    [Fact]
    public void Locale_mapping_and_unconfigured_detection()
    {
        Assert.Equal("en-GB", AzureSpeechService.ToLocale("en"));
        Assert.Equal("en-US", AzureSpeechService.ToLocale("en-US"));
        Assert.False(new AzureSpeechOptions("", "k").IsConfigured);
        Assert.False(new AzureSpeechOptions("https://x", " ").IsConfigured);
    }

    private static string Phrases(params (int Spk, int Start, int Dur, string Text, string Locale, double Conf)[] p) =>
        "{\"phrases\":[" + string.Join(",", p.Select(x =>
            $"{{\"speaker\":{x.Spk},\"offsetMilliseconds\":{x.Start},\"durationMilliseconds\":{x.Dur},\"text\":\"{x.Text}\",\"locale\":\"{x.Locale}\",\"confidence\":{x.Conf}}}")) + "]}";

    [Fact]
    public void Consecutive_phrases_from_one_speaker_become_one_paragraph()
    {
        var r = AzureSpeechService.Parse(Phrases(
            (1, 2000, 1500, "Hi, Ashley here.", "en-GB", 0.9),
            (1, 5000, 1000, "I'm running a test with Matthew.", "en-GB", 0.9),
            (1, 6000, 2000, "I'm going to ask a few questions.", "en-GB", 0.9),
            (2, 18000, 3000, "Manchester United will win.", "en-GB", 0.9),
            (1, 22000, 2000, "Who is your favourite player?", "en-GB", 0.9)), Opts);
        Assert.Equal(3, r.Segments.Count);
        Assert.Equal("Hi, Ashley here. I'm running a test with Matthew. I'm going to ask a few questions.", r.Segments[0].Text);
        Assert.Equal((2000, 8000), (r.Segments[0].StartMs, r.Segments[0].EndMs));
        Assert.Equal(r.Segments[0].SpeakerId, r.Segments[2].SpeakerId);
        Assert.NotEqual(r.Segments[0].SpeakerId, r.Segments[1].SpeakerId);
    }

    [Fact]
    public void A_language_change_a_long_pause_or_a_very_long_paragraph_starts_a_new_one()
    {
        var langChange = AzureSpeechService.Parse(Phrases((1, 0, 1000, "Hello.", "en-GB", 0.9), (1, 1000, 1000, "Bonjour.", "fr-FR", 0.9)), Opts);
        Assert.Equal(2, langChange.Segments.Count);

        var pause = AzureSpeechService.Parse(Phrases((1, 0, 1000, "First.", "en-GB", 0.9), (1, 1000 + AzureSpeechService.MaxGapMs, 1000, "Same gap.", "en-GB", 0.9),
            (1, 1000 + AzureSpeechService.MaxGapMs + 1000 + AzureSpeechService.MaxGapMs + 1, 1000, "Too late.", "en-GB", 0.9)), Opts);
        Assert.Equal(2, pause.Segments.Count);
        Assert.Equal("First. Same gap.", pause.Segments[0].Text);

        var big = new string('x', 500);
        var cap = AzureSpeechService.Parse(Phrases((1, 0, 1000, big, "en-GB", 0.9), (1, 1000, 1000, big, "en-GB", 0.9)), Opts);
        Assert.Equal(2, cap.Segments.Count);
    }

    [Fact]
    public void A_paragraph_is_low_confidence_if_any_phrase_in_it_was_and_keeps_the_lowest_score()
    {
        var r = AzureSpeechService.Parse(Phrases((1, 0, 1000, "Clear.", "en-GB", 0.95), (1, 1000, 1000, "mumble", "en-GB", 0.3), (1, 2000, 1000, "Clear again.", "en-GB", 0.9)), Opts);
        var seg = Assert.Single(r.Segments);
        Assert.True(seg.LowConfidence);
        Assert.Equal(0.3, seg.Confidence);
        Assert.Equal("Clear. mumble Clear again.", seg.Text);
    }

    [Fact]
    public void Word_timings_are_kept_and_joined_when_phrases_merge()
    {
        const string json = """
        {"phrases":[
         {"speaker":1,"offsetMilliseconds":1000,"durationMilliseconds":1000,"text":"Hello there.","locale":"en-GB","confidence":0.9,
          "words":[{"text":"Hello","offsetMilliseconds":1000,"durationMilliseconds":400},{"text":"there.","offsetMilliseconds":1500,"durationMilliseconds":500}]},
         {"speaker":1,"offsetMilliseconds":2500,"durationMilliseconds":800,"text":"Good day.","locale":"en-GB","confidence":0.9,
          "words":[{"text":"Good","offsetMilliseconds":2500,"durationMilliseconds":300},{"text":"day.","offsetMilliseconds":2900,"durationMilliseconds":400}]}]}
        """;
        var seg = Assert.Single(AzureSpeechService.Parse(json, Opts).Segments);
        Assert.Equal("Hello there. Good day.", seg.Text);
        Assert.Equal(["Hello", "there.", "Good", "day."], seg.Words!.Select(w => w.Text));
        Assert.Equal((1000, 1400), (seg.Words![0].StartMs, seg.Words![0].EndMs));
        Assert.Equal((2900, 3300), (seg.Words![3].StartMs, seg.Words![3].EndMs));
    }

    [Fact]
    public async Task The_vocabulary_is_sent_as_a_phrase_list_and_left_out_when_empty()
    {
        var h = new Handler((_, _) => Json(HttpStatusCode.OK, Good));
        await Make(h).TranscribeAsync(new MemoryStream([1]), Opts with { Phrases = ["Amarin", "Bruno Fernandes", "amarin"] }, default);
        Assert.Contains("\"phraseList\":{\"phrases\":[\"Amarin\",\"Bruno Fernandes\"]}", h.Calls[0].Body);

        var h2 = new Handler((_, _) => Json(HttpStatusCode.OK, Good));
        await Make(h2).TranscribeAsync(new MemoryStream([1]), Opts with { Phrases = [] }, default);
        Assert.DoesNotContain("phraseList", h2.Calls[0].Body);
    }

    [Fact]
    public async Task If_azure_rejects_the_vocabulary_the_recording_is_transcribed_without_it_and_flagged()
    {
        var h = new Handler((_, body) => body.Contains("phraseList")
            ? Json(HttpStatusCode.BadRequest, """{"error":{"message":"phraseList is not supported with multiple locales"}}""")
            : Json(HttpStatusCode.OK, Good));
        var r = await Make(h).TranscribeAsync(new MemoryStream([1]), Opts with { Phrases = ["Amarin"] }, default);
        Assert.Equal(2, h.Calls.Count);
        Assert.DoesNotContain("phraseList", h.Calls[1].Body);
        Assert.Equal(3, r.Segments.Count);
        Assert.Contains("without it", r.Warning);
    }

    [Fact]
    public async Task A_bad_request_with_no_vocabulary_is_still_an_error()
    {
        var h = new Handler((_, _) => Json(HttpStatusCode.BadRequest, """{"error":{"message":"unsupported audio"}}"""));
        var ex = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => Make(h).TranscribeAsync(new MemoryStream([1]), Opts, default));
        Assert.Single(h.Calls);
        Assert.Contains("unsupported audio", ex.Message);
    }

    private sealed class ForwardOnly(Stream inner) : Stream
    {
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] b, int o, int c) => inner.Read(b, o, c);
        public override void Flush() { }
        public override long Seek(long o, SeekOrigin so) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    }

    [Fact]
    public async Task Too_many_requests_waits_as_long_as_azure_asks_and_keeps_trying_for_minutes()
    {
        var waits = new List<TimeSpan>();
        var n = 0;
        var h = new Handler((_, _) =>
        {
            if (++n > 3) return Json(HttpStatusCode.OK, Good);
            var res = Json(HttpStatusCode.TooManyRequests, "{}");
            if (n == 1) res.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(40));
            return res;
        });
        var svc = new AzureSpeechService(new HttpClient(h), new AzureSpeechOptions("https://demo.cognitiveservices.azure.com/", "k"))
        { Delay = (t, _) => { waits.Add(t); return Task.CompletedTask; } };
        await svc.TranscribeAsync(new MemoryStream([1]), Opts, default);
        Assert.Equal([TimeSpan.FromSeconds(40), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60)], waits);
    }

    [Fact]
    public async Task A_persistent_429_is_classified_as_busy_for_support()
    {
        var h = new Handler((_, _) => Json(HttpStatusCode.TooManyRequests, "{}"));
        var ex = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => Make(h).TranscribeAsync(new MemoryStream([1]), Opts, default));
        Assert.Equal(6, h.Calls.Count);
        Assert.Equal(FaultKind.Busy, ((ProviderException)ex).Kind);
        Assert.Contains("S0", ex.Message); // the technical text for support keeps the pricing-tier hint
    }

    [Fact]
    public async Task A_request_timeout_from_azure_is_retried_and_counts_as_unreachable()
    {
        var n = 0;
        var h = new Handler((_, _) => ++n < 3 ? Json(HttpStatusCode.RequestTimeout, "{\"error\":{\"code\":\"Timeout\",\"message\":\"The operation was timeout.\"}}") : Json(HttpStatusCode.OK, Good));
        var r = await Make(h).TranscribeAsync(new MemoryStream([1]), Opts, default);
        Assert.Equal(3, h.Calls.Count);
        Assert.NotEmpty(r.Segments);
        Assert.Equal(FaultKind.Unavailable, VerbaFlow.Infrastructure.Services.FailureReporter.ForStatus(HttpStatusCode.RequestTimeout));
    }

    [Fact]
    public void With_no_candidate_languages_no_locales_are_sent_so_azure_detects_the_language()
    {
        var def = JsonDocument.Parse(AzureSpeechService.BuildDefinition(new TranscribeOptions([], 8, true))).RootElement;
        Assert.False(def.TryGetProperty("locales", out _));
        Assert.True(def.GetProperty("diarization").GetProperty("enabled").GetBoolean());
    }

    private static string Phrase(int speaker, int start, int len, string text, string locale, double conf) =>
        $$"""{ "speaker": {{speaker}}, "offsetMilliseconds": {{start}}, "durationMilliseconds": {{len}}, "text": "{{text}}", "locale": "{{locale}}", "confidence": {{conf.ToString(System.Globalization.CultureInfo.InvariantCulture)}} }""";

    [Fact]
    public async Task Two_languages_are_transcribed_separately_and_each_stretch_keeps_the_more_confident_version()
    {
        // English model: good on the English, weak on the Afrikaans, and it drops the last stretch entirely.
        var en = $$"""{ "phrases": [ {{Phrase(1, 0, 2000, "Good morning everyone.", "en-ZA", 0.93)}}, {{Phrase(2, 2500, 2000, "Gear more ah", "en-ZA", 0.41)}} ] }""";
        // Afrikaans model: weak on the English, good on the Afrikaans, and hears the last stretch.
        var af = $$"""{ "phrases": [ {{Phrase(1, 0, 2000, "Goeie more almal.", "af-ZA", 0.52)}}, {{Phrase(2, 2400, 2100, "Goeie more, hoe gaan dit?", "af-ZA", 0.9)}}, {{Phrase(1, 6000, 1000, "Baie dankie.", "af-ZA", 0.88)}} ] }""";
        var h = new Handler((_, body) => Json(HttpStatusCode.OK, body.Contains("\"af-ZA\"") ? af : en));
        var r = await Make(h).TranscribeAsync(new MemoryStream([1, 2, 3]), new(["en-ZA", "af-ZA"], 2, true), default);

        Assert.Equal(2, h.Calls.Count);
        Assert.Contains("\"locales\":[\"en-ZA\"]", h.Calls[0].Body);
        Assert.Contains("\"locales\":[\"af-ZA\"]", h.Calls[1].Body);
        Assert.Equal(["Good morning everyone.", "Goeie more, hoe gaan dit?", "Baie dankie."], r.Segments.Select(s => s.Text));
        Assert.Equal(["en", "af", "af"], r.Segments.Select(s => s.Language));
        Assert.Equal(r.Segments[0].SpeakerId, r.Segments[2].SpeakerId); // Speaker 1 is the same label in both runs
        Assert.NotEqual(r.Segments[0].SpeakerId, r.Segments[1].SpeakerId);
        Assert.Equal(2, r.Speakers.Count);
        Assert.EndsWith("+per-language", r.Engine);
        Assert.Equal("en-ZA: 1 kept of 2, af-ZA: 2 kept of 3", r.Detail);
    }

    [Fact]
    public void A_stretch_goes_to_the_first_language_when_confidence_is_equal()
    {
        var s1 = new VerbaFlow.Core.Transcripts.Segment(Guid.NewGuid(), Guid.NewGuid(), "en", 0, 1000, "One", 0.8, false);
        var s2 = s1 with { Id = Guid.NewGuid(), Language = "af", Text = "Een" };
        var (chosen, kept) = AzureSpeechService.CombineLanguages([[s1], [s2]]);
        Assert.Equal("One", Assert.Single(chosen).Text);
        Assert.Equal([1, 0], kept);
    }
}
