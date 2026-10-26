using VerbaFlow.Core.Domain;
using VerbaFlow.Infrastructure.Services;

namespace VerbaFlow.Tests.Services;

public class RetentionTests
{
    private static RetentionService Service(Env env) => new(env.Stores, env.Meeting, env.Clock, env.Reporter);

    private static string[] AudioFiles(Env env) =>
        Directory.GetFiles(env.Dir, "*.bin", SearchOption.AllDirectories).Where(f => !f.Contains(".quarantine")).ToArray();

    /// <summary>Carol proposes, Dave agrees, then the week's grace passes.</summary>
    private static async Task AgreeAsync(Env env, RetentionService r, RetentionRules meetings, RetentionRules? dictations = null, bool passGrace = true)
    {
        await r.ProposeAsync(env.Carol, new RetentionChange(meetings, dictations, "Policy from the board"));
        await r.AgreeAsync(env.Dave);
        if (passGrace) env.Clock.Now += RetentionPolicy.Grace + TimeSpan.FromMinutes(1);
    }

    [Fact]
    public async Task By_default_nothing_is_deleted()
    {
        using var env = new Env();
        var r = Service(env);
        var id = await env.ProcessedRecordingAsync();
        await env.Meeting.ApproveAsync(env.Alice, id);
        env.Clock.Now += TimeSpan.FromDays(5000);
        Assert.Equal(0, await r.ApplyAsync());
        Assert.Equal(ItemStatus.Completed, (await env.Item(id)).Status);
        Assert.Null((await r.ForItemAsync(env.Alice, id)).Note);
    }

    [Fact]
    public async Task A_change_needs_a_second_different_administrator()
    {
        using var env = new Env();
        var r = Service(env);
        var rules = new RetentionRules(DeleteApprovedAfterDays: 30);
        await Assert.ThrowsAsync<ForbiddenException>(() => r.ProposeAsync(env.Alice, new(rules, null, null)));
        await r.ProposeAsync(env.Carol, new(rules, null, null));
        Assert.True((await r.PolicyAsync()).Meetings.KeepsEverything); // not yet
        await Assert.ThrowsAsync<DomainException>(() => r.AgreeAsync(env.Carol));
        await Assert.ThrowsAsync<ForbiddenException>(() => r.AgreeAsync(env.Bob));
        await Assert.ThrowsAsync<DomainException>(() => r.ProposeAsync(env.Dave, new(rules, null, null))); // one change at a time
        var view = await r.AgreeAsync(env.Dave);
        Assert.Equal(30, view.Meetings.DeleteApprovedAfterDays);
        Assert.Equal(env.Clock.Now + RetentionPolicy.Grace, view.AppliesFrom);
        Assert.Equal(["Carol", "Dave"], view.AgreedBy);
        Assert.Null(view.Proposal);

        var types = (await env.Stores.Audit.ListAsync()).Select(e => e.Type).ToList();
        Assert.Contains("retention.proposed", types);
        Assert.Contains("retention.agreed", types);
    }

    [Fact]
    public async Task A_proposal_can_be_withdrawn_or_rejected_and_bad_numbers_are_refused()
    {
        using var env = new Env();
        var r = Service(env);
        await Assert.ThrowsAsync<DomainException>(() => r.ProposeAsync(env.Carol, new(new(DeleteApprovedAfterDays: 0), null, null)));
        await Assert.ThrowsAsync<DomainException>(() => r.ProposeAsync(env.Carol, new(new(DeleteApprovedAfterDays: 30, RemoveRecordingAfterDays: 30), null, null)));
        await Assert.ThrowsAsync<DomainException>(() => r.ProposeAsync(env.Carol, new(null, null, null))); // same as now
        await r.ProposeAsync(env.Carol, new(new(DeleteApprovedAfterDays: 30), null, null));
        Assert.True((await r.OverviewAsync(env.Carol)).Proposal!.YouProposed);
        await r.DropProposalAsync(env.Carol);
        await r.ProposeAsync(env.Carol, new(new(DeleteApprovedAfterDays: 30), null, null));
        await r.DropProposalAsync(env.Dave);
        Assert.True((await r.PolicyAsync()).Meetings.KeepsEverything);
        var types = (await env.Stores.Audit.ListAsync()).Select(e => e.Type).ToList();
        Assert.Contains("retention.withdrawn", types);
        Assert.Contains("retention.rejected", types);
    }

    [Fact]
    public async Task An_approved_meeting_is_deleted_after_the_days_but_not_before_the_week_of_grace()
    {
        using var env = new Env();
        var r = Service(env);
        var id = await env.ProcessedRecordingAsync();
        await env.Meeting.ApproveAsync(env.Alice, id);
        env.Clock.Now += TimeSpan.FromDays(100); // long past 30 days already
        await AgreeAsync(env, r, new(DeleteApprovedAfterDays: 30), passGrace: false);

        Assert.Equal(0, await r.ApplyAsync());
        Assert.Contains("will be deleted on", (await r.ForItemAsync(env.Alice, id)).Note);
        var due = (await r.OverviewAsync(env.Carol)).Due.Single();
        Assert.Equal(RetentionService.Delete, due.Action);
        Assert.Empty((await r.OverviewAsync(env.Alice)).Due); // only administrators see the list

        env.Clock.Now += RetentionPolicy.Grace + TimeSpan.FromMinutes(1);
        Assert.Equal(1, await r.ApplyAsync());
        var item = await env.Item(id);
        Assert.Equal(ItemStatus.Purged, item.Status);
        Assert.Contains("Retention rules", item.DeleteReason);
        Assert.Empty(AudioFiles(env));
        Assert.Null(await env.Stores.Transcripts.GetAsync(id));
        Assert.Equal("the retention rules", (await env.Meeting.GetItemAsync(env.Alice, id)).DeletedBy);
        Assert.Equal("item.deleted", (await env.Meeting.GetAuditAsync(env.Alice, id))[^1].Type);
        Assert.True((await env.Stores.Audit.VerifyAsync()).Ok);
        Assert.Equal(0, await r.ApplyAsync()); // nothing twice
    }

    [Fact]
    public async Task Removing_the_recording_keeps_the_text_and_stops_playing_and_converting_again()
    {
        using var env = new Env();
        var r = Service(env);
        var id = await env.ProcessedRecordingAsync();
        await env.Meeting.ApproveAsync(env.Alice, id);
        await AgreeAsync(env, r, new(RemoveRecordingAfterDays: 3));

        Assert.Equal(1, await r.ApplyAsync());
        var item = await env.Item(id);
        Assert.Equal(ItemStatus.Completed, item.Status);
        Assert.Empty(AudioFiles(env));
        Assert.NotNull(await env.Stores.Transcripts.GetAsync(id));
        var ex = await Assert.ThrowsAsync<NotFoundException>(() => env.Meeting.OpenAudioAsync(env.Alice, id, false));
        Assert.Contains("retention rules", ex.Message);
        var detail = await env.Meeting.GetItemAsync(env.Alice, id);
        Assert.Contains("The text is kept", detail.RecordingRemoved);
        Assert.Contains("The text is kept", (await r.ForItemAsync(env.Alice, id)).Note);
        Assert.Equal("retention.recording_removed", (await env.Meeting.GetAuditAsync(env.Alice, id))[^1].Type);
        Assert.Equal(0, await r.ApplyAsync());

        // Reopened, the new version has no recording to convert again either.
        var req = await env.Meeting.RequestReopenAsync(env.Alice, id, "Typo");
        await env.Meeting.ApproveReopenAsync(env.Carol, req);
        var next = (await env.Meeting.ApproveReopenAsync(env.Dave, req))!.Value;
        Assert.DoesNotContain("rerun", (await env.Meeting.GetItemAsync(env.Alice, next)).Row.Actions);
        await Assert.ThrowsAsync<DomainException>(() => env.Meeting.RerunAsync(env.Alice, next, null, discardEdits: true));
    }

    [Fact]
    public async Task Never_approved_items_go_after_the_days_without_any_activity()
    {
        using var env = new Env();
        var r = Service(env);
        var id = await env.ProcessedRecordingAsync();
        await AgreeAsync(env, r, new(DeleteUnapprovedAfterDays: 20));
        env.Clock.Now += TimeSpan.FromDays(15);
        await env.Meeting.EditSegmentAsync(env.Alice, id, (await env.Meeting.GetTranscriptAsync(env.Alice, id)).Segments[0].Id, "Edited later.");
        env.Clock.Now += TimeSpan.FromDays(10); // 25 days since it was made, 10 since the edit
        Assert.Equal(0, await r.ApplyAsync());
        env.Clock.Now += TimeSpan.FromDays(11);
        Assert.Equal(1, await r.ApplyAsync());
        Assert.Equal(ItemStatus.Purged, (await env.Item(id)).Status);
    }

    [Fact]
    public async Task Dictations_follow_their_own_rules()
    {
        using var env = new Env();
        var r = Service(env);
        var meeting = await env.ProcessedRecordingAsync();
        var dictation = await env.Meeting.CreateRecordingAsync(env.Alice, env.Dictation());
        await env.Processing.ProcessAsync(dictation);
        await env.Meeting.ApproveAsync(env.Alice, meeting);
        await env.Meeting.ApproveAsync(env.Alice, dictation);
        await AgreeAsync(env, r, new(), new(DeleteApprovedAfterDays: 1));
        Assert.Equal(1, await r.ApplyAsync());
        Assert.Equal(ItemStatus.Purged, (await env.Item(dictation)).Status);
        Assert.Equal(ItemStatus.Completed, (await env.Item(meeting)).Status);
    }

    [Fact]
    public async Task A_held_recording_is_never_deleted_until_released()
    {
        using var env = new Env();
        var r = Service(env);
        var id = await env.ProcessedRecordingAsync();
        await env.Meeting.ApproveAsync(env.Alice, id);
        await AgreeAsync(env, r, new(DeleteApprovedAfterDays: 1));

        await Assert.ThrowsAsync<ForbiddenException>(() => r.HoldAsync(env.Alice, id, "Complaint"));
        await Assert.ThrowsAsync<DomainException>(() => r.HoldAsync(env.Carol, id, " "));
        var held = await r.HoldAsync(env.Carol, id, "Complaint from the client");
        Assert.True(held.Held);
        Assert.Contains("On hold", held.Note);
        Assert.Single((await r.OverviewAsync(env.Dave)).Holds);
        Assert.Equal(0, await r.ApplyAsync());
        Assert.Equal(ItemStatus.Completed, (await env.Item(id)).Status);

        await r.ReleaseAsync(env.Dave, id);
        Assert.Equal(1, await r.ApplyAsync());
        Assert.Equal(ItemStatus.Purged, (await env.Item(id)).Status);
        var types = (await env.Stores.Audit.ListAsync()).Select(e => e.Type).ToList();
        Assert.Contains("retention.held", types);
        Assert.Contains("retention.released", types);
    }
}
