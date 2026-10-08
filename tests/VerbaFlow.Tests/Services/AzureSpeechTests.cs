using System.Net;
using System.Text;
using System.Text.Json;
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

    private static readonly TranscribeOptions Opts = new(["en", "fr"], 8, true);

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
        Assert.Contains("\"locales\":[\"en-GB\",\"fr-FR\"]", body);
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
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Make(h).TranscribeAsync(new MemoryStream([1]), Opts, default));
        Assert.Equal(5, h.Calls.Count);
        Assert.Contains("503", ex.Message);
    }

    [Fact]
    public async Task A_rejected_key_fails_at_once_with_a_clear_message_that_does_not_repeat_the_key()
    {
        var h = new Handler((_, _) => Json(HttpStatusCode.Unauthorized, """{"error":{"message":"Access denied due to invalid subscription key."}}"""));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Make(h).TranscribeAsync(new MemoryStream([1]), Opts, default));
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
}
