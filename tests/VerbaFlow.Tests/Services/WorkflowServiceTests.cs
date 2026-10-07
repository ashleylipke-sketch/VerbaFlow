using Microsoft.Data.Sqlite;
using VerbaFlow.Core.Domain;
using VerbaFlow.Infrastructure.Services;

namespace VerbaFlow.Tests.Services;

public class WorkflowServiceTests
{
    [Fact]
    public async Task The_full_happy_path_from_recording_to_approval_leaves_a_verifiable_trail()
    {
        using var env = new Env();
        var id = await env.ProcessedRecordingAsync();
        await env.Meeting.UpdateDetailsAsync(env.Alice, id, new DetailsUpdate("Board meeting Q3", "CLI-001", "Minutes", Priority.High, new DateOnly(2026, 10, 20), false));
        await env.Meeting.AssignAsync(env.Alice, id, env.Bob.Id, null);
        Assert.Equal(ItemStatus.AwaitingAssignee, (await env.Item(id)).Status);
        await env.Meeting.AcceptAsync(env.Bob, id);

        var t = await env.Meeting.GetTranscriptAsync(env.Bob, id);
        await env.Meeting.EditSegmentAsync(env.Bob, id, t.Segments[0].Id, "A corrected opening line.");
        var after = await env.Meeting.GetTranscriptAsync(env.Bob, id);
        Assert.Equal(2, after.VersionNo);
        Assert.Equal("assignee", after.Versions[^1].Capacity);
        Assert.Equal("MachineV1", after.Versions[0].Kind);

        await env.Meeting.ReturnAsync(env.Bob, id);
        Assert.Equal(ItemStatus.WithAuthor, (await env.Item(id)).Status);
        await env.Meeting.ApproveAsync(env.Alice, id);
        Assert.Equal(ItemStatus.Completed, (await env.Item(id)).Status);

        var trail = await env.Meeting.GetAuditAsync(env.Alice, id);
        Assert.Equal(["item.created", "processing.started", "processing.completed", "item.details_updated", "item.assigned",
            "item.opened_by_assignee", "transcript.edited", "item.returned_to_owner", "item.approved"],
            trail.Select(e => e.Type).ToArray());
        Assert.True((await env.Meeting.VerifyAuditAsync(env.Carol)).Ok);
    }

    [Fact]
    public async Task After_approval_nothing_can_be_changed()
    {
        using var env = new Env();
        var id = await env.ProcessedRecordingAsync();
        await env.Meeting.ApproveAsync(env.Alice, id);
        var seg = (await env.Meeting.GetTranscriptAsync(env.Alice, id)).Segments[0];
        await Assert.ThrowsAsync<ForbiddenException>(() => env.Meeting.EditSegmentAsync(env.Alice, id, seg.Id, "Changed"));
        await Assert.ThrowsAsync<ForbiddenException>(() => env.Meeting.EditSegmentAsync(env.Carol, id, seg.Id, "Changed by admin"));
        await Assert.ThrowsAsync<ForbiddenException>(() => env.Meeting.AssignAsync(env.Carol, id, env.Bob.Id, null));
        await Assert.ThrowsAsync<ForbiddenException>(() => env.Meeting.UpdateDetailsAsync(env.Alice, id, new DetailsUpdate("x", null, null, null, null, false)));
    }

    [Fact]
    public async Task The_database_itself_refuses_to_change_an_approved_item_or_the_audit_trail()
    {
        using var env = new Env();
        var id = await env.ProcessedRecordingAsync();
        await env.Meeting.ApproveAsync(env.Alice, id);
        Assert.Throws<SqliteException>(() => env.Execute($"UPDATE doc_items SET json = '{{}}' WHERE id = '{id}'"));
        Assert.Throws<SqliteException>(() => env.Execute($"DELETE FROM doc_items WHERE id = '{id}'"));
        Assert.Throws<SqliteException>(() => env.Execute("UPDATE audit_events SET details = '{}' WHERE seq = 1"));
        Assert.Throws<SqliteException>(() => env.Execute("DELETE FROM audit_events WHERE seq = 1"));
        Assert.Throws<SqliteException>(() => env.Execute("UPDATE doc_media SET json = '{}'"));
        Assert.Throws<SqliteException>(() => env.Execute("DELETE FROM doc_media"));
    }

    [Fact]
    public async Task Even_if_database_protection_were_bypassed_the_chain_check_detects_tampering()
    {
        using var env = new Env();
        var id = await env.ProcessedRecordingAsync();
        await env.Meeting.ApproveAsync(env.Alice, id);
        env.Execute("DROP TRIGGER audit_no_update");
        env.Execute("UPDATE audit_events SET details = '{\"forged\":true}' WHERE seq = 2");
        var result = await env.Meeting.VerifyAuditAsync(env.Carol);
        Assert.False(result.Ok);
        Assert.Equal(2, result.BadSeq);
    }

    [Fact]
    public async Task Self_assigned_items_stay_in_With_Author_with_the_tag_and_filter_correctly()
    {
        using var env = new Env();
        var self = await env.ProcessedRecordingAsync();
        var other = await env.ProcessedRecordingAsync();
        await env.Meeting.AssignAsync(env.Alice, self, env.Alice.Id, null);
        await env.Meeting.AssignAsync(env.Alice, other, env.Bob.Id, null);

        var rows = await env.Meeting.ListAsync(env.Alice, new ListQuery(null, null, null));
        var selfRow = rows.Single(r => r.Id == self);
        Assert.Equal("With Author", selfRow.Status);
        Assert.Contains("Self-assigned", selfRow.Tags);
        Assert.Equal("Awaiting Assignee", rows.Single(r => r.Id == other).Status);
        Assert.Single(await env.Meeting.ListAsync(env.Alice, new ListQuery(null, true, null)));
        Assert.Single(await env.Meeting.ListAsync(env.Alice, new ListQuery(null, false, null)));
    }

    [Fact]
    public async Task Views_can_be_included_or_excluded_by_status()
    {
        using var env = new Env();
        var a = await env.ProcessedRecordingAsync();
        var b = await env.ProcessedRecordingAsync();
        await env.Meeting.ApproveAsync(env.Alice, b);
        var active = await env.Meeting.ListAsync(env.Alice, new ListQuery(new HashSet<ItemStatus> { ItemStatus.WithAuthor }, null, null));
        Assert.Equal([a], active.Select(r => r.Id));
        var all = await env.Meeting.ListAsync(env.Alice, new ListQuery(null, null, null));
        Assert.Equal(2, all.Count);
    }

    [Fact]
    public async Task Visibility_authors_see_their_own_assignees_see_theirs_admins_see_all_others_none()
    {
        using var env = new Env();
        var id = await env.ProcessedRecordingAsync();
        await env.Meeting.AssignAsync(env.Alice, id, env.Bob.Id, null);
        Assert.Single(await env.Meeting.ListAsync(env.Alice, new ListQuery(null, null, null)));
        Assert.Single(await env.Meeting.ListAsync(env.Bob, new ListQuery(null, null, null)));
        Assert.Single(await env.Meeting.ListAsync(env.Carol, new ListQuery(null, null, null)));
        Assert.Empty(await env.Meeting.ListAsync(env.Xavier, new ListQuery(null, null, null)));
        await Assert.ThrowsAsync<ForbiddenException>(() => env.Meeting.GetItemAsync(env.Xavier, id));
    }

    [Fact]
    public async Task Assignees_can_play_the_audio_but_not_download_it_without_the_separate_right()
    {
        using var env = new Env();
        var id = await env.ProcessedRecordingAsync();
        await env.Meeting.AssignAsync(env.Alice, id, env.Bob.Id, null);
        await env.Meeting.AcceptAsync(env.Bob, id);
        await using (var play = await env.Meeting.OpenAudioAsync(env.Bob, id, download: false)) Assert.NotNull(play.Content);
        await Assert.ThrowsAsync<ForbiddenException>(() => env.Meeting.OpenAudioAsync(env.Bob, id, download: true));
        var trail = await env.Meeting.GetAuditAsync(env.Alice, id);
        Assert.Contains(trail, e => e.Type == "audio.played");
    }

    [Fact]
    public async Task Only_the_author_or_an_admin_can_reassign_and_a_reason_is_needed_when_changing_assignee()
    {
        using var env = new Env();
        var id = await env.ProcessedRecordingAsync();
        await env.Meeting.AssignAsync(env.Alice, id, env.Bob.Id, null);
        await Assert.ThrowsAsync<ForbiddenException>(() => env.Meeting.AssignAsync(env.Bob, id, env.Xavier.Id, "x"));
        await Assert.ThrowsAsync<DomainException>(() => env.Meeting.AssignAsync(env.Alice, id, env.Xavier.Id, null));
        await env.Meeting.AssignAsync(env.Carol, id, env.Xavier.Id, "Bob is on sick leave");
        Assert.Equal(env.Xavier.Id, (await env.Item(id)).AssignedUserId);
        var trail = await env.Meeting.GetAuditAsync(env.Alice, id);
        Assert.Contains(trail, e => e.Type == "item.assigned" && e.Details.Contains("sick leave"));
    }

    [Fact]
    public async Task Separation_of_duties_when_enabled_blocks_self_approval_by_non_admins()
    {
        using var env = new Env(new PlatformPolicy(BlockSelfApproval: true));
        var id = await env.ProcessedRecordingAsync();
        await env.Meeting.AssignAsync(env.Alice, id, env.Alice.Id, null);
        await Assert.ThrowsAsync<DomainException>(() => env.Meeting.ApproveAsync(env.Alice, id));
        await env.Meeting.ApproveAsync(env.Carol, id);
        Assert.Equal(ItemStatus.Completed, (await env.Item(id)).Status);
    }

    [Fact]
    public async Task Items_cannot_be_approved_or_assigned_while_still_processing()
    {
        using var env = new Env();
        var id = await env.Meeting.CreateRecordingAsync(env.Alice, env.Recording());
        await Assert.ThrowsAsync<DomainException>(() => env.Meeting.ApproveAsync(env.Alice, id));
        await Assert.ThrowsAsync<DomainException>(() => env.Meeting.AssignAsync(env.Alice, id, env.Bob.Id, null));
    }

    [Fact]
    public async Task Editing_the_transcript_is_not_allowed_for_an_assignee_who_has_not_opened_the_item()
    {
        using var env = new Env();
        var id = await env.ProcessedRecordingAsync();
        await env.Meeting.AssignAsync(env.Alice, id, env.Bob.Id, null);
        var seg = (await env.Meeting.GetTranscriptAsync(env.Bob, id)).Segments[0];
        await Assert.ThrowsAsync<ForbiddenException>(() => env.Meeting.EditSegmentAsync(env.Bob, id, seg.Id, "Too early"));
    }

    [Fact]
    public async Task Renaming_a_speaker_applies_across_the_transcript_and_is_a_version()
    {
        using var env = new Env();
        var id = await env.ProcessedRecordingAsync();
        var t = await env.Meeting.GetTranscriptAsync(env.Alice, id);
        var first = t.Speakers[0];
        await env.Meeting.RenameSpeakerAsync(env.Alice, id, first.Id, "Alice Smith");
        var after = await env.Meeting.GetTranscriptAsync(env.Alice, id);
        Assert.All(after.Segments.Where(s => s.SpeakerId == first.Id), s => Assert.Equal("Alice Smith", s.Speaker));
        Assert.Equal("SpeakerCorrection", after.Versions[^1].Kind);
    }
}
