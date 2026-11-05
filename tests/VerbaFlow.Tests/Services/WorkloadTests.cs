using VerbaFlow.Core.Domain;
using VerbaFlow.Infrastructure.Services;

namespace VerbaFlow.Tests.Services;

public class WorkloadTests
{
    [Fact]
    public async Task Shows_who_has_what_how_long_steps_took_and_what_is_overdue()
    {
        using var env = new Env();
        var w = new WorkloadService(env.Stores, env.Clock);
        var done = await env.ProcessedRecordingAsync();
        var late = await env.ProcessedRecordingAsync();
        var waiting = await env.ProcessedRecordingAsync();

        // One goes all the way: assigned, accepted 2 h later, typed for 4 h, approved 1 h after it came back.
        await env.Meeting.AssignAsync(env.Alice, done, env.Bob.Id, null);
        env.Clock.Now = env.Clock.Now.AddHours(2);
        await env.Meeting.AcceptAsync(env.Bob, done);
        env.Clock.Now = env.Clock.Now.AddHours(4);
        await env.Meeting.ReturnAsync(env.Bob, done);
        env.Clock.Now = env.Clock.Now.AddHours(1);
        await env.Meeting.ApproveAsync(env.Alice, done);

        // One is with Bob and overdue; one is still with Alice.
        await env.Meeting.UpdateDetailsAsync(env.Alice, late, new DetailsUpdate(null, null, null, null, DateOnly.FromDateTime(env.Clock.Now.LocalDateTime.Date).AddDays(-3), false));
        await env.Meeting.AssignAsync(env.Alice, late, env.Bob.Id, null);

        var v = await w.OverviewAsync(env.Carol, null, 30);
        Assert.Equal(2, v.Open);
        Assert.Equal(1, v.Overdue);
        Assert.Equal(1, v.ApprovedInPeriod);
        var bob = Assert.Single(v.People, p => p.UserId == env.Bob.Id);
        Assert.Equal((1, 1, 2, 1), (bob.ToAccept, bob.Overdue, bob.AssignedToThem, bob.Returned));
        Assert.Equal(2, bob.HoursToAccept);
        Assert.Equal(4, bob.HoursTyping);
        var alice = Assert.Single(v.People, p => p.UserId == env.Alice.Id);
        Assert.Equal((1, 1), (alice.AsAuthor, alice.Approved));
        Assert.Equal(1, alice.HoursToApprove);
        var o = Assert.Single(v.OverdueItems);
        Assert.Equal((late, "Bob", "To accept", 3), (o.ItemId, o.Holder, o.Stage, o.DaysLate));
        Assert.Contains(v.WaitingLongest, x => x.ItemId == waiting);

        Assert.Equal(0, (await w.OverviewAsync(env.Carol, "dictate", 30)).Open); // only meetings so far
        env.Clock.Now = env.Clock.Now.AddDays(10);
        Assert.Equal(0, (await w.OverviewAsync(env.Carol, null, 7)).ApprovedInPeriod); // the approval is older than a week
        await Assert.ThrowsAsync<ForbiddenException>(() => w.OverviewAsync(env.Alice, null, 30));
    }
}
