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

    [Fact]
    public async Task A_due_date_can_be_set_with_an_assignment_or_on_its_own_until_approval()
    {
        using var env = new Env();
        var bulk = new BulkActions(env.Meeting, env.Stores, env.Reporter);
        var today = DateOnly.FromDateTime(env.Clock.GetUtcNow().UtcDateTime);
        var a = await env.ProcessedRecordingAsync();
        var b = await env.ProcessedRecordingAsync();

        await env.Meeting.AssignAsync(env.Alice, a, env.Bob.Id, null, today.AddDays(3));
        Assert.Equal(today.AddDays(3), (await env.Stores.Items.GetAsync(a))!.DueDate);
        Assert.Contains((await env.Stores.Audit.ListAsync(a)), e => e.Type == "item.assigned" && e.Details.Contains(today.AddDays(3).ToString("yyyy-MM-dd")));

        var r = await bulk.RunAsync(env.Alice, "assign", [b], env.Bob.Id, null, today.AddDays(1));
        Assert.True(r[0].Done);
        Assert.Equal(today.AddDays(1), (await env.Stores.Items.GetAsync(b))!.DueDate);

        // On its own: the owner can change or remove it; the assignee and strangers cannot.
        Assert.True((await env.Meeting.GetItemAsync(env.Alice, a)).CanSetDueDate);
        Assert.False((await env.Meeting.GetItemAsync(env.Bob, a)).CanSetDueDate);
        await Assert.ThrowsAsync<ForbiddenException>(() => env.Meeting.SetDueDateAsync(env.Bob, a, today));
        await Assert.ThrowsAsync<DomainException>(() => env.Meeting.SetDueDateAsync(env.Alice, a, today.AddDays(-10)));
        await env.Meeting.SetDueDateAsync(env.Alice, a, null);
        Assert.Null((await env.Stores.Items.GetAsync(a))!.DueDate);
        Assert.Contains((await env.Stores.Audit.ListAsync(a)), e => e.Type == "item.due_date_set");

        // Once approved, the date is fixed. (Taking it back keeps the date.)
        await env.Meeting.AssignAsync(env.Alice, b, null, "Doing it myself");
        Assert.Equal(today.AddDays(1), (await env.Stores.Items.GetAsync(b))!.DueDate);
        await env.Meeting.ApproveAsync(env.Alice, b);
        Assert.False((await env.Meeting.GetItemAsync(env.Alice, b)).CanSetDueDate);
        await Assert.ThrowsAsync<ForbiddenException>(() => env.Meeting.SetDueDateAsync(env.Alice, b, today.AddDays(2)));
    }
}
