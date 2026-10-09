using System.Net;
using VerbaFlow.Core.Domain;
using VerbaFlow.Infrastructure.Services;

namespace VerbaFlow.Tests.Services;

public class CustomerErrorTests
{
    private sealed class Boom(string message, FaultKind? kind = null) : Exception(message) { public FaultKind? Kind { get; } = kind; }

    [Theory]
    [InlineData(FaultKind.Busy, "busy")]
    [InlineData(FaultKind.Unavailable, "could not be reached")]
    [InlineData(FaultKind.Settings, "contact support")]
    [InlineData(FaultKind.Audio, "recording could not be processed")]
    [InlineData(FaultKind.Blocked, "summary could not be created")]
    [InlineData(FaultKind.Other, "Something went wrong")]
    public void Customer_messages_are_plain_carry_a_reference_and_never_mention_pricing_or_code(FaultKind kind, string expected)
    {
        var msg = CustomerMessages.For(kind, "transcription", "VF-ABC234");
        Assert.Contains(expected, msg);
        Assert.EndsWith("Reference: VF-ABC234.", msg);
        foreach (var banned in new[] { "Azure", "key", "endpoint", "tier", "S0", "F0", "429", "Exception", "ffmpeg", "deployment", "user-secrets" })
            Assert.DoesNotContain(banned, msg, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void References_are_short_readable_and_different_each_time()
    {
        var refs = Enumerable.Range(0, 50).Select(_ => CustomerMessages.NewReference()).ToList();
        Assert.All(refs, r => Assert.Matches("^VF-[2-9A-HJKMNP-Z]{6}$", r));
        Assert.Equal(50, refs.Distinct().Count());
    }

    [Fact]
    public void Statuses_map_to_kinds()
    {
        Assert.Equal(FaultKind.Busy, FailureReporter.ForStatus(HttpStatusCode.TooManyRequests));
        Assert.Equal(FaultKind.Unavailable, FailureReporter.ForStatus(HttpStatusCode.ServiceUnavailable));
        Assert.Equal(FaultKind.Settings, FailureReporter.ForStatus(HttpStatusCode.Unauthorized));
        Assert.Equal(FaultKind.Audio, FailureReporter.ForStatus(HttpStatusCode.BadRequest));
    }

    [Fact]
    public async Task A_failed_transcription_shows_the_customer_a_plain_message_and_keeps_the_detail_for_support()
    {
        using var env = new Env();
        env.Speech.Fail = true; // throws "speech service unavailable"
        var id = await env.Meeting.CreateRecordingAsync(env.Alice, env.Recording());
        await env.Processing.ProcessAsync(id);

        var detail = await env.Meeting.GetItemAsync(env.Carol, id); // an administrator of the customer
        Assert.NotNull(detail.FailureReason);
        Assert.Contains("Reference: VF-", detail.FailureReason);
        Assert.DoesNotContain("speech service unavailable", detail.FailureReason);

        var reference = System.Text.RegularExpressions.Regex.Match(detail.FailureReason!, "VF-[A-Z0-9]{6}").Value;
        var stored = (await env.Stores.SupportErrors.ListAsync()).Single(e => e.Reference == reference);
        Assert.Contains("speech service unavailable", stored.Detail);
        Assert.Equal(id, stored.ItemId);

        // The history shows the kind and reference, not the technical text, to anyone who can read the item.
        var failed = (await env.Meeting.GetAuditAsync(env.Carol, id)).Single(a => a.Type == "processing.failed");
        Assert.Contains(reference, failed.Details);
        Assert.DoesNotContain("speech service unavailable", failed.Details);
    }

    [Fact]
    public async Task A_summary_failure_is_also_kept_out_of_the_history_and_findable_by_reference()
    {
        using var env = new Env();
        env.Ai.Fail = true;
        var id = await env.ProcessedRecordingAsync();
        var failed = (await env.Stores.Audit.ListAsync(id)).Single(a => a.Type == "outputs.failed");
        Assert.DoesNotContain("ai service unavailable", failed.Details);
        var reference = System.Text.RegularExpressions.Regex.Match(failed.Details, "VF-[A-Z0-9]{6}").Value;
        Assert.Contains("ai service unavailable", (await env.Stores.SupportErrors.ListAsync()).Single(e => e.Reference == reference).Detail);
    }

    [Fact]
    public void Network_failures_count_as_unreachable_and_unknown_ones_as_other()
    {
        Assert.Equal(FaultKind.Unavailable, FailureReporter.Classify(new HttpRequestException("dns")));
        Assert.Equal(FaultKind.Other, FailureReporter.Classify(new Boom("x")));
        Assert.Equal(FaultKind.Busy, FailureReporter.Classify(new ProviderException(FaultKind.Busy, "x")));
    }
}
