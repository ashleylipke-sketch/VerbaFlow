using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using VerbaFlow.Core.Domain;
using VerbaFlow.Core.Providers;
using VerbaFlow.Core.Transcripts;
using VerbaFlow.Infrastructure.Google;
using VerbaFlow.Infrastructure.Services;
using VerbaFlow.Infrastructure.StandIns;

namespace VerbaFlow.Tests.Services;

/// <summary>isiXhosa, Sepedi, Setswana, siSwati, Xitsonga and Tshivenda through Google Speech-to-Text.</summary>
public class SouthAfricanLanguageTests
{
    private const string UserFile = """{"type":"authorized_user","client_id":"cid","client_secret":"csecret","refresh_token":"rtoken","quota_project_id":"from-file"}""";

    /// <summary>Plays Google: answers the token request and each recognize request, and remembers what it was sent.</summary>
    private sealed class FakeGoogle : HttpMessageHandler
    {
        public List<(string Url, string Body, HttpRequestMessage Request)> Calls { get; } = [];
        public Func<JsonNode, int, (HttpStatusCode, string)>? Answer { get; set; }
        public int Tokens { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Calls.Add((request.RequestUri!.ToString(), body, request));
            if (request.RequestUri!.Host == "oauth2.googleapis.com")
            {
                Tokens++;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"access_token":"tok-1","expires_in":3599}""") };
            }
            var n = Calls.Count(c => c.Url.Contains(":recognize")) - 1;
            var (status, text) = Answer?.Invoke(JsonNode.Parse(body)!, n) ?? (HttpStatusCode.OK, """
                {"results":[{"alternatives":[{"transcript":"Molo enkosi","confidence":0.9,"words":[
                  {"word":"Molo","startOffset":"0.500s","endOffset":"0.900s"},{"word":"enkosi","startOffset":"1s","endOffset":"1.400s"}]}],
                  "resultEndOffset":"1.500s","languageCode":"xh-za"}]}
                """);
            return new HttpResponseMessage(status) { Content = new StringContent(text) };
        }
    }

    private static GoogleSpeechService Service(FakeGoogle fake, int seconds, string file = UserFile) =>
        new(new HttpClient(fake), new GoogleSpeechOptions("my-project", "eu", GoogleCredentials.Parse(file)), null)
        {
            Delay = (_, _) => Task.CompletedTask,
            ToPcm = (_, _) => Task.FromResult(Tone(seconds)),
        };

    /// <summary>A loud tone with a quiet tenth of a second at 47 s, where a piece should be cut.</summary>
    private static byte[] Tone(int seconds)
    {
        var pcm = new byte[seconds * 16000 * 2];
        for (var i = 0; i < pcm.Length / 2; i++)
        {
            var quiet = i >= 47 * 16000 && i < 47 * 16000 + 1600;
            var v = quiet ? (short)0 : (short)(8000 * Math.Sin(i * 0.1));
            pcm[2 * i] = (byte)v; pcm[2 * i + 1] = (byte)(v >> 8);
        }
        return pcm;
    }

    [Fact]
    public void A_Google_language_can_be_chosen_alone_but_not_with_another_and_Sesotho_is_still_refused()
    {
        var item = Item.CreateDraft(Guid.NewGuid(), Guid.NewGuid(), "Imbizo", 60_000, "en", new DateTimeOffset(2026, 11, 12, 9, 0, 0, TimeSpan.Zero));
        item.SetSpokenLanguages("xh-ZA");
        Assert.Equal("xh-ZA", item.SpokenLanguages);
        Assert.True(SpokenLanguageCatalogue.IsGoogle("tn-ZA"));
        Assert.False(SpokenLanguageCatalogue.IsGoogle("af-ZA"));
        var mixed = Assert.Throws<DomainException>(() => item.SetSpokenLanguages("en-ZA,xh-ZA"));
        Assert.Contains("isiXhosa must be the only language", mixed.Message);
        Assert.Throws<DomainException>(() => item.SetSpokenLanguages("st-ZA"));
        Assert.Throws<DomainException>(() => item.SetSpokenLanguages("nr-ZA"));
        Assert.Throws<DomainException>(() => MeetService.NormaliseLanguages("ve-ZA,en-GB"));
        Assert.False(SpokenLanguageCatalogue.Find("ts-ZA")!.Live);
    }

    [Fact]
    public async Task Live_text_is_refused_for_a_Google_language()
    {
        using var env = new Env();
        var live = new LiveSessions(env.Stores, new StandInLiveSpeech(), env.Clock);
        var e = await Assert.ThrowsAsync<DomainException>(() => live.StartAsync(env.Alice, "nso-ZA"));
        Assert.Contains("no live text", e.Message);
    }

    [Fact]
    public async Task A_long_recording_goes_to_Google_EU_in_pieces_under_a_minute_signed_in_with_gcloud()
    {
        var fake = new FakeGoogle();
        var r = await Service(fake, 70).TranscribeAsync(new MemoryStream(), new TranscribeOptions(["tn-ZA"], 1, false, ["Gaborone"]), default);

        var calls = fake.Calls.Where(c => c.Url.Contains(":recognize")).ToList();
        Assert.Equal(2, calls.Count);
        Assert.Equal("https://eu-speech.googleapis.com/v2/projects/my-project/locations/eu/recognizers/_:recognize", calls[0].Url);
        Assert.Equal(1, fake.Tokens); // the token is kept between pieces
        Assert.Contains("refresh_token=rtoken", fake.Calls[0].Body);
        Assert.Equal("Bearer tok-1", calls[0].Request.Headers.Authorization!.ToString());
        Assert.Equal("my-project", calls[0].Request.Headers.GetValues("x-goog-user-project").Single());
        var config = JsonNode.Parse(calls[0].Body)!["config"]!;
        Assert.Equal("tn-Latn-ZA", config["languageCodes"]![0]!.GetValue<string>());
        Assert.Equal("long", config["model"]!.GetValue<string>());
        Assert.True(config["features"]!["enableWordTimeOffsets"]!.GetValue<bool>());
        // cut at the quiet moment at 47 s: the first piece holds 47.05 s of samples
        var first = Convert.FromBase64String(JsonNode.Parse(calls[0].Body)!["content"]!.GetValue<string>());
        Assert.InRange(first.Length / 32, 47_000, 47_100);

        Assert.Equal("google-speech-v2-long", r.Engine);
        Assert.Single(r.Speakers);
        Assert.Equal(2, r.Segments.Count);
        Assert.Equal("tn-ZA", r.Segments[0].Language);
        Assert.Equal(500, r.Segments[0].StartMs);
        Assert.InRange(r.Segments[1].StartMs, first.Length / 32 + 499, first.Length / 32 + 501); // second piece's times moved to the whole recording
        Assert.Equal(["Molo", "enkosi"], r.Segments[1].Words!.Select(w => w.Text));
        Assert.Contains("2 pieces", r.Detail);
        Assert.Contains("custom vocabulary not used", r.Detail);
    }

    [Fact]
    public async Task When_Google_refuses_word_timings_the_piece_is_asked_again_plainly()
    {
        var fake = new FakeGoogle
        {
            Answer = (body, n) => body["config"]!["features"] is not null
                ? (HttpStatusCode.BadRequest, """{"error":{"message":"Recognizer does not support feature: word_level_timestamps"}}""")
                : (HttpStatusCode.OK, """{"results":[{"alternatives":[{"transcript":"Ndaa"}],"resultEndOffset":"2.100s"}]}"""),
        };
        var r = await Service(fake, 5).TranscribeAsync(new MemoryStream(), new TranscribeOptions(["ve-ZA"], 1, false), default);
        Assert.Equal("Ndaa", Assert.Single(r.Segments).Text);
        Assert.Equal(2100, r.Segments[0].EndMs);
        Assert.Null(r.Segments[0].Words);
        Assert.Contains("without word timings", r.Detail);
    }

    [Fact]
    public async Task A_refusal_is_a_provider_failure_for_support_and_busy_answers_are_retried()
    {
        var fake = new FakeGoogle { Answer = (_, n) => n < 2 ? (HttpStatusCode.ServiceUnavailable, "{}") : (HttpStatusCode.OK, "{}") };
        var r = await Service(fake, 3).TranscribeAsync(new MemoryStream(), new TranscribeOptions(["xh-ZA"], 1, false), default);
        Assert.Empty(r.Segments);
        Assert.Equal(3, fake.Calls.Count(c => c.Url.Contains(":recognize")));

        var denied = new FakeGoogle { Answer = (_, _) => (HttpStatusCode.Forbidden, """{"error":{"message":"Cloud Speech-to-Text API has not been used in project"}}""") };
        var e = await Assert.ThrowsAsync<ProviderException>(() => Service(denied, 3).TranscribeAsync(new MemoryStream(), new TranscribeOptions(["xh-ZA"], 1, false), default));
        Assert.Equal(FaultKind.Settings, e.Kind);
    }

    [Fact]
    public async Task A_service_account_key_signs_in_with_a_statement_signed_by_its_key()
    {
        using var rsa = RSA.Create(2048);
        var key = new JsonObject
        {
            ["type"] = "service_account", ["project_id"] = "p", ["client_email"] = "speech@p.iam.gserviceaccount.com",
            ["private_key"] = rsa.ExportPkcs8PrivateKeyPem(),
        }.ToJsonString();
        var fake = new FakeGoogle();
        var service = Service(fake, 2, key);
        await service.TranscribeAsync(new MemoryStream(), new TranscribeOptions(["ss-ZA"], 1, false), default);
        var form = fake.Calls[0].Body;
        Assert.Contains("grant_type=urn%3Aietf%3Aparams%3Aoauth%3Agrant-type%3Ajwt-bearer", form);
        var jwt = Uri.UnescapeDataString(form.Split("assertion=")[1].Split('&')[0]).Split('.');
        static byte[] B64(string s) => Convert.FromBase64String(s.Replace('-', '+').Replace('_', '/').PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
        Assert.True(rsa.VerifyData(Encoding.ASCII.GetBytes(jwt[0] + "." + jwt[1]), B64(jwt[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        var claims = JsonNode.Parse(B64(jwt[1]))!;
        Assert.Equal("speech@p.iam.gserviceaccount.com", claims["iss"]!.GetValue<string>());
        Assert.Equal("https://www.googleapis.com/auth/cloud-platform", claims["scope"]!.GetValue<string>());
        Assert.False(fake.Calls[1].Request.Headers.Contains("x-goog-user-project")); // a service account pays from its own project
        Assert.Throws<InvalidOperationException>(() => GoogleCredentials.Parse("""{"type":"external_account"}"""));
    }

    private sealed class Recorder(string engine) : ISpeechService
    {
        public TranscribeOptions? Last { get; private set; }
        public Task<TranscriptionResult> TranscribeAsync(Stream audio, TranscribeOptions options, CancellationToken ct)
        {
            Last = options;
            var one = new Speaker(Guid.NewGuid(), "Speaker 1");
            Segment S(int from, int to, string text) => new(Guid.NewGuid(), one.Id, options.CandidateLanguages.FirstOrDefault() ?? "en", from, to, text, 1, false,
                [new WordTiming(text, from, to)]);
            return Task.FromResult(new TranscriptionResult(engine, [one], [S(0, 4000, "Molweni"), S(5000, 9000, "Enkosi")], Detail: "Google long, 1 piece"));
        }
    }

    private sealed class TwoVoices : ISpeakerDiarizer
    {
        public int? ToldTo { get; private set; }
        public string? Method { get; private set; }
        public string Name => "local";
        public Task<IReadOnlyList<SpeakerTurn>> DiarizeAsync(Stream audio, int? numSpeakers, string? method, CancellationToken ct)
        {
            ToldTo = numSpeakers; Method = method;
            return Task.FromResult<IReadOnlyList<SpeakerTurn>>([new(0, 4500, 0), new(4500, 9000, 1)]);
        }
    }

    [Fact]
    public async Task A_meeting_in_isiXhosa_goes_to_Google_and_gets_its_speakers_from_the_local_voice_model_even_with_a_count()
    {
        using var env = new Env();
        var azure = new Recorder("azure"); var google = new Recorder("google-speech-v2-chirp_3");
        var voices = new TwoVoices();
        var processing = new ProcessingService(env.Stores, new SpeechRouter(azure, google), env.Ai, new StandInAudioEnhancer(), env.Clock, env.Reporter,
            voices, new DiarizationOptions("x", null, MinSpeakerSeconds: 0f));
        var id = await env.Meeting.CreateRecordingAsync(env.Alice, env.Recording() with { SpokenLanguages = "xh-ZA", NumSpeakers = 2 });
        await processing.ProcessAsync(id);

        Assert.Null(azure.Last);
        Assert.False(google.Last!.Diarize);
        Assert.Equal(2, voices.ToldTo);
        Assert.Null(voices.Method); // "azure" (speech service's own labels) means nothing to Google
        var t = await env.Meeting.GetTranscriptAsync(env.Alice, id);
        Assert.Equal(2, t.Speakers.Count);
        var done = (await env.Stores.Audit.ListAsync(id)).Last(a => a.Type == "processing.completed");
        Assert.Contains("google-speech-v2-chirp_3\\u002Blocal", done.Details);
        Assert.Contains("\"googleSpeech\":\"Google long, 1 piece\"", done.Details);
        Assert.Contains("\"groupingMethod\":\"standard\"", done.Details);
    }

    [Fact]
    public async Task A_dictation_in_Setswana_skips_word_for_word_and_Azure_languages_are_unchanged()
    {
        using var env = new Env();
        var azure = new Recorder("azure"); var google = new Recorder("google-speech-v2-long");
        var processing = new ProcessingService(env.Stores, new SpeechRouter(azure, google), env.Ai, new StandInAudioEnhancer(), env.Clock, env.Reporter,
            wordForWord: new NeverWordForWord());
        var id = await env.Meeting.CreateRecordingAsync(env.Alice, env.Dictation() with { SpokenLanguages = "tn-ZA" });
        await processing.ProcessAsync(id);
        Assert.NotNull(google.Last);
        var done = (await env.Stores.Audit.ListAsync(id)).Last(a => a.Type == "processing.completed");
        Assert.Contains("Google (the only service for this language)", done.Details);

        var english = await env.Meeting.CreateRecordingAsync(env.Alice, env.Recording() with { SpokenLanguages = "en-ZA" });
        await processing.ProcessAsync(english);
        Assert.Equal(["en-ZA"], azure.Last!.CandidateLanguages);
    }

    private sealed class NeverWordForWord : IDictationSpeech
    {
        public string Name => "never";
        public Task<TranscriptionResult> TranscribeAsync(Stream audio, TranscribeOptions options, CancellationToken ct) =>
            throw new InvalidOperationException("word for word must not be used for a Google language");
    }

    [Fact]
    public async Task Without_Google_set_up_the_router_refuses_plainly_for_support()
    {
        var router = new SpeechRouter(new Recorder("azure"), null);
        var e = await Assert.ThrowsAsync<ProviderException>(() => router.TranscribeAsync(new MemoryStream(), new TranscribeOptions(["ts-ZA"], 1, true), default));
        Assert.Equal(FaultKind.Settings, e.Kind);
    }
}
