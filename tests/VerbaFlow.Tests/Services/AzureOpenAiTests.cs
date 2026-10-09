using System.Net;
using System.Text;
using System.Text.Json;
using VerbaFlow.Core.Domain;
using VerbaFlow.Core.Transcripts;
using VerbaFlow.Infrastructure.Azure;

namespace VerbaFlow.Tests.Services;

public class AzureOpenAiTests
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

    private static string Reply(string summary = "A short summary.", string[]? actions = null, string finish = "stop", string? refusal = null)
    {
        var content = JsonSerializer.Serialize(new { summary, actionPoints = actions ?? ["Alice: send the report, by Friday"], minutes = "Discussion:\n- Budget", tone = "Calm and constructive." });
        return JsonSerializer.Serialize(new { choices = new[] { new { finish_reason = finish, message = new { role = "assistant", content, refusal } } } });
    }

    private static AzureOpenAiOutputService Make(Handler h, int max = 80_000) =>
        new(new HttpClient(h), new AzureOpenAiOptions("https://demo.openai.azure.com/", "secret-key-123", "my-deploy"))
        { Delay = (_, _) => Task.CompletedTask, MaxTranscriptChars = max };

    private static List<RenderedSegment> Transcript(int lines = 2, string text = "We agreed the budget.")
    {
        var sp = Guid.NewGuid();
        return Enumerable.Range(0, lines).Select(i => new RenderedSegment(
            new Segment(Guid.NewGuid(), sp, "en", 65_000 + i * 1000, 66_000 + i * 1000, text, 0.9, false), i % 2 == 0 ? "Alice" : "Bob")).ToList();
    }

    [Fact]
    public async Task Sends_the_documented_request_and_never_puts_the_key_in_the_url_or_body()
    {
        var h = new Handler((_, _) => Json(HttpStatusCode.OK, Reply()));
        var r = await Make(h).GenerateAsync(Transcript(), "fr", default);
        var (req, body) = Assert.Single(h.Calls);
        Assert.Equal("https://demo.openai.azure.com/openai/v1/chat/completions", req.RequestUri!.ToString());
        Assert.Equal("secret-key-123", req.Headers.GetValues("api-key").Single());
        Assert.DoesNotContain("secret-key-123", req.RequestUri.ToString() + body);
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        Assert.Equal("my-deploy", root.GetProperty("model").GetString());
        var fmt = root.GetProperty("response_format");
        Assert.Equal("json_schema", fmt.GetProperty("type").GetString());
        Assert.True(fmt.GetProperty("json_schema").GetProperty("strict").GetBoolean());
        var msgs = root.GetProperty("messages");
        var system = msgs[0].GetProperty("content").GetString()!;
        var user = msgs[1].GetProperty("content").GetString()!;
        Assert.Contains("French", system);
        Assert.DoesNotContain("We agreed", system);
        Assert.StartsWith("<transcript>", user);
        Assert.Contains("[01:05] Alice: We agreed the budget.", user);
        Assert.Contains("[01:06] Bob:", user);
        Assert.Equal("azure-openai:my-deploy", r.Engine);
        Assert.Equal("fr", r.Language);
        Assert.Equal("A short summary.", r.Summary);
        Assert.Equal(["Alice: send the report, by Friday"], r.ActionPoints);
        Assert.Equal("Calm and constructive.", r.ToneOfMeeting);
    }

    [Fact]
    public async Task English_output_is_British_English()
    {
        var h = new Handler((_, _) => Json(HttpStatusCode.OK, Reply()));
        await Make(h).GenerateAsync(Transcript(), "en", default);
        Assert.Contains("British English", h.Calls[0].Body);
    }

    [Fact]
    public async Task A_refusal_is_reported_not_used_as_a_summary()
    {
        var h = new Handler((_, _) => Json(HttpStatusCode.OK, Reply(refusal: "I can't help with that.")));
        var ex = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => Make(h).GenerateAsync(Transcript(), "en", default));
        Assert.Contains("declined", ex.Message);
    }

    [Theory]
    [InlineData("length", "too long")]
    [InlineData("content_filter", "content filter")]
    public async Task A_reply_that_did_not_finish_normally_is_refused(string finish, string expected)
    {
        var h = new Handler((_, _) => Json(HttpStatusCode.OK, Reply(finish: finish)));
        var ex = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => Make(h).GenerateAsync(Transcript(), "en", default));
        Assert.Contains(expected, ex.Message);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{\"choices\":[]}")]
    [InlineData("{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"content\":\"{\\\"summary\\\":\\\"x\\\"}\"}}]}")]
    public async Task Malformed_replies_give_a_plain_error(string body)
    {
        var h = new Handler((_, _) => Json(HttpStatusCode.OK, body));
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => Make(h).GenerateAsync(Transcript(), "en", default));
    }

    [Fact]
    public async Task Busy_responses_are_retried_then_succeed()
    {
        var n = 0;
        var h = new Handler((_, _) => ++n < 3 ? Json(HttpStatusCode.TooManyRequests, "{}") : Json(HttpStatusCode.OK, Reply()));
        var r = await Make(h).GenerateAsync(Transcript(), "en", default);
        Assert.Equal(3, h.Calls.Count);
        Assert.Equal("A short summary.", r.Summary);
    }

    [Fact]
    public async Task A_rejected_key_gives_advice_and_never_echoes_the_key()
    {
        var h = new Handler((_, _) => Json(HttpStatusCode.Unauthorized, "{\"error\":{\"message\":\"Access denied\"}}"));
        var ex = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => Make(h).GenerateAsync(Transcript(), "en", default));
        Assert.Single(h.Calls);
        Assert.Contains("Check the Azure OpenAI key", ex.Message);
        Assert.DoesNotContain("secret-key-123", ex.Message);
    }

    [Fact]
    public async Task Long_transcripts_are_summarised_in_parts_then_combined()
    {
        var h = new Handler((_, _) => Json(HttpStatusCode.OK, Reply()));
        // 30 lines of ~37 chars with a 400-char limit gives several chunks.
        var r = await Make(h, 400).GenerateAsync(Transcript(30), "en", default);
        Assert.True(h.Calls.Count >= 4);
        static string User(string body) => JsonDocument.Parse(body).RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!;
        static string System(string body) => JsonDocument.Parse(body).RootElement.GetProperty("messages")[0].GetProperty("content").GetString()!;
        Assert.Contains("<partials>", User(h.Calls[^1].Body));
        Assert.Contains("combine", System(h.Calls[^1].Body));
        Assert.All(h.Calls.Take(h.Calls.Count - 1), c => Assert.Contains("<transcript>", User(c.Body)));
        Assert.Equal("A short summary.", r.Summary);
    }

    [Fact]
    public async Task An_empty_transcript_is_refused_without_calling_azure()
    {
        var h = new Handler((_, _) => Json(HttpStatusCode.OK, Reply()));
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => Make(h).GenerateAsync([], "en", default));
        Assert.Empty(h.Calls);
    }

    // ---------- how the app uses it ----------

    [Fact]
    public async Task A_summary_failure_does_not_fail_the_item()
    {
        using var env = new Env();
        env.Ai.Fail = true;
        var id = await env.ProcessedRecordingAsync();
        Assert.Equal(ProcessingState.Idle, (await env.Item(id)).Processing);
        Assert.Null(await env.Meeting.GetOutputsAsync(env.Alice, id));
        Assert.Contains(await env.Stores.Audit.ListAsync(id), a => a.Type == "outputs.failed");
    }

    [Fact]
    public async Task Regenerating_replaces_the_summary_and_uses_the_current_speaker_names()
    {
        using var env = new Env();
        var id = await env.ProcessedRecordingAsync();
        var first = (await env.Meeting.GetOutputsAsync(env.Alice, id))!.Summary;
        var t = await env.Meeting.GetTranscriptAsync(env.Alice, id);
        await env.Meeting.RenameSpeakerAsync(env.Alice, id, t.Speakers[0].Id, "Zebediah");
        await env.Meeting.RegenerateOutputsAsync(env.Alice, id);
        var again = (await env.Meeting.GetOutputsAsync(env.Alice, id))!.Summary;
        Assert.NotEqual(first, again);
        Assert.Contains("Zebediah", again);
        Assert.Contains(await env.Stores.Audit.ListAsync(id), a => a.Type == "outputs.regenerated");
    }

    [Fact]
    public async Task Regenerating_can_rescue_an_item_whose_first_summary_failed()
    {
        using var env = new Env();
        env.Ai.Fail = true;
        var id = await env.ProcessedRecordingAsync();
        env.Ai.Fail = false;
        await env.Meeting.RegenerateOutputsAsync(env.Alice, id);
        Assert.NotNull(await env.Meeting.GetOutputsAsync(env.Alice, id));
    }

    [Fact]
    public async Task A_regeneration_failure_keeps_the_old_summary_and_is_audited()
    {
        using var env = new Env();
        var id = await env.ProcessedRecordingAsync();
        var before = (await env.Meeting.GetOutputsAsync(env.Alice, id))!.Summary;
        env.Ai.Fail = true;
        var ex = await Assert.ThrowsAsync<DomainException>(() => env.Meeting.RegenerateOutputsAsync(env.Alice, id));
        Assert.Contains("Reference: VF-", ex.Message);
        Assert.DoesNotContain("ai service unavailable", ex.Message); // the technical text stays with support
        Assert.Equal(before, (await env.Meeting.GetOutputsAsync(env.Alice, id))!.Summary);
        Assert.Contains(await env.Stores.Audit.ListAsync(id), a => a.Type == "outputs.failed");
    }

    [Fact]
    public async Task People_without_write_access_and_approved_items_cannot_regenerate()
    {
        using var env = new Env();
        var id = await env.ProcessedRecordingAsync();
        await Assert.ThrowsAnyAsync<Exception>(() => env.Meeting.RegenerateOutputsAsync(env.Xavier, id));
        await env.Meeting.ApproveAsync(env.Alice, id);
        var calls = env.Ai.Calls;
        await Assert.ThrowsAnyAsync<Exception>(() => env.Meeting.RegenerateOutputsAsync(env.Alice, id));
        Assert.Equal(calls, env.Ai.Calls);
    }
}

public class AuthorColumnTests
{
    [Fact]
    public async Task The_list_names_the_author_even_after_the_item_is_assigned_to_someone_else()
    {
        using var env = new Env();
        var id = await env.ProcessedRecordingAsync(env.Alice);
        await env.Meeting.AssignAsync(env.Alice, id, env.Bob.Id, null);
        var row = (await env.Meeting.ListAsync(env.Alice, new VerbaFlow.Infrastructure.Services.ListQuery(null, null, null))).Single(r => r.Id == id);
        Assert.Equal("Alice", row.Author);
        Assert.Equal("Bob", row.AssignedTo);
    }
}
