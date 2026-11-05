using VerbaFlow.Core.Domain;
using VerbaFlow.Infrastructure.Services;

namespace VerbaFlow.Tests.Services;

public class BulkActionTests
{
    [Fact]
    public async Task Each_ticked_recording_gets_the_usual_checks_and_one_refusal_does_not_stop_the_rest()
    {
        using var env = new Env();
        var bulk = new BulkActions(env.Meeting, env.Stores, env.Reporter);
        var a = await env.ProcessedRecordingAsync();
        var b = await env.ProcessedRecordingAsync();
        var xaviers = await env.ProcessedRecordingAsync(env.Xavier);

        var r = await bulk.RunAsync(env.Alice, "assign", [a, b, xaviers], env.Bob.Id, null);
        Assert.Equal([true, true, false], r.Select(x => x.Done));
        Assert.Equal("(a recording you cannot open)", r[2].Name); // never shows another person's recording name
        Assert.Equal(env.Bob.Id, (await env.Stores.Items.GetAsync(a))!.AssignedUserId);

        r = await bulk.RunAsync(env.Bob, "accept", [a, b], null, null);
        Assert.All(r, x => Assert.True(x.Done));
        r = await bulk.RunAsync(env.Bob, "return", [a, b], null, null);
        Assert.All(r, x => Assert.True(x.Done));

        // Re-assigning someone else's work needs a reason, one recording at a time or ticked together.
        await bulk.RunAsync(env.Alice, "assign", [a], env.Bob.Id, null);
        r = await bulk.RunAsync(env.Alice, "assign", [a], env.Carol.Id, null);
        Assert.False(r[0].Done);
        Assert.Contains("reason", r[0].Problem);

        r = await bulk.RunAsync(env.Alice, "approve", [b], null, null);
        Assert.True(r[0].Done);
        Assert.Equal(ItemStatus.Completed, (await env.Stores.Items.GetAsync(b))!.Status);
        Assert.Equal(1, (await env.Stores.Audit.ListAsync(b)).Count(e => e.Type == "item.approved"));

        await Assert.ThrowsAsync<DomainException>(() => bulk.RunAsync(env.Alice, "delete", [a], null, null));
        await Assert.ThrowsAsync<DomainException>(() => bulk.RunAsync(env.Alice, "approve", [], null, null));
    }
}
