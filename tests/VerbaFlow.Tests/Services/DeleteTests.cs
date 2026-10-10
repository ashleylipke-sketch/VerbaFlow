using Microsoft.Data.Sqlite;
using VerbaFlow.Core.Domain;

namespace VerbaFlow.Tests.Services;

public class DeleteTests
{
    private static async Task<(Env env, Guid original, Guid reopened)> ReopenedAsync()
    {
        var env = new Env();
        var id = await env.ProcessedRecordingAsync();
        await env.Meeting.ApproveAsync(env.Alice, id);
        var req = await env.Meeting.RequestReopenAsync(env.Alice, id, "Wrong recording");
        await env.Meeting.ApproveReopenAsync(env.Carol, req);
        var next = (await env.Meeting.ApproveReopenAsync(env.Dave, req))!.Value;
        return (env, id, next);
    }

    private static string[] AudioFiles(Env env) =>
        Directory.GetFiles(env.Dir, "*.bin", SearchOption.AllDirectories).Where(f => !f.Contains(".quarantine")).ToArray();

    [Fact]
    public async Task The_author_deletes_an_unapproved_recording_and_its_audio_transcript_and_summary_go_but_the_history_stays()
    {
        using var env = new Env();
        var id = await env.ProcessedRecordingAsync();
        Assert.NotEmpty(AudioFiles(env));
        Assert.Contains("delete", (await env.Meeting.GetItemAsync(env.Alice, id)).Row.Actions);

        await env.Meeting.DeleteAsync(env.Alice, id, "Test recording");

        var item = await env.Item(id);
        Assert.Equal(ItemStatus.Purged, item.Status);
        Assert.Equal("Test recording", item.DeleteReason);
        Assert.Equal(env.Alice.Id, item.DeletedBy);
        Assert.Empty(AudioFiles(env));
        Assert.Null(await env.Stores.Transcripts.GetAsync(id));
        Assert.Null(await env.Stores.Outputs.GetAsync(id));
        Assert.NotNull(await env.Stores.Media.FindOriginalAsync(id)); // the fingerprint record stays
        await Assert.ThrowsAsync<NotFoundException>(() => env.Meeting.OpenAudioAsync(env.Alice, id, false));
        var trail = await env.Meeting.GetAuditAsync(env.Alice, id);
        Assert.Equal("item.deleted", trail[^1].Type);
        Assert.Contains("Test recording", trail[^1].Details);
        Assert.True((await env.Stores.Audit.VerifyAsync()).Ok);

        var row = (await env.Meeting.ListAsync(env.Alice, new(null, null, null))).Single(r => r.Id == id);
        Assert.Equal("Deleted", row.Status);
        Assert.DoesNotContain("delete", row.Actions);
        await Assert.ThrowsAsync<DomainException>(() => env.Meeting.DeleteAsync(env.Alice, id, "again"));
        await Assert.ThrowsAnyAsync<Exception>(() => env.Meeting.EditSegmentAsync(env.Alice, id, Guid.NewGuid(), "x"));
    }

    [Fact]
    public async Task An_admin_may_delete_but_the_assignee_or_anyone_else_may_not_and_a_reason_is_required()
    {
        using var env = new Env();
        var id = await env.ProcessedRecordingAsync();
        await Assert.ThrowsAsync<ForbiddenException>(() => env.Meeting.DeleteAsync(env.Bob, id, "no"));
        await Assert.ThrowsAsync<DomainException>(() => env.Meeting.DeleteAsync(env.Alice, id, " "));
        await env.Meeting.DeleteAsync(env.Carol, id, "Admin clean-up");
        Assert.Equal(ItemStatus.Purged, (await env.Item(id)).Status);
    }

    [Fact]
    public async Task An_approved_recording_cannot_be_deleted_until_it_is_reopened()
    {
        using var env = new Env();
        var id = await env.ProcessedRecordingAsync();
        await env.Meeting.ApproveAsync(env.Alice, id);
        Assert.DoesNotContain("delete", (await env.Meeting.GetItemAsync(env.Alice, id)).Row.Actions);
        var ex = await Assert.ThrowsAsync<DomainException>(() => env.Meeting.DeleteAsync(env.Carol, id, "x"));
        Assert.Contains("reopened first", ex.Message);
        await Assert.ThrowsAsync<DomainException>(() => env.Meeting.RequestDeleteAsync(env.Carol, id, "x"));
    }

    [Fact]
    public async Task A_reopened_recording_needs_two_different_admins_and_then_every_version_is_deleted()
    {
        var (env, original, reopened) = await ReopenedAsync();
        using var _ = env;
        Assert.Contains("request-delete", (await env.Meeting.GetItemAsync(env.Alice, reopened)).Row.Actions);
        await Assert.ThrowsAsync<DomainException>(() => env.Meeting.DeleteAsync(env.Alice, reopened, "x")); // not on its own
        await Assert.ThrowsAsync<DomainException>(() => env.Meeting.RequestDeleteAsync(env.Alice, original, "x")); // only the newest version

        var req = await env.Meeting.RequestDeleteAsync(env.Alice, reopened, "Recorded the wrong meeting");
        await Assert.ThrowsAsync<DomainException>(() => env.Meeting.RequestDeleteAsync(env.Alice, reopened, "twice"));
        await Assert.ThrowsAsync<DomainException>(() => env.Meeting.ApproveDeleteAsync(env.Bob, req)); // not an admin
        Assert.False(await env.Meeting.ApproveDeleteAsync(env.Carol, req));
        await Assert.ThrowsAsync<DomainException>(() => env.Meeting.ApproveDeleteAsync(env.Carol, req)); // same admin twice
        Assert.Equal(ItemStatus.WithAuthor, (await env.Item(reopened)).Status);

        Assert.True(await env.Meeting.ApproveDeleteAsync(env.Dave, req));
        Assert.Equal(ItemStatus.Purged, (await env.Item(reopened)).Status);
        Assert.Equal(ItemStatus.Purged, (await env.Item(original)).Status); // the approved version goes too
        Assert.Empty(AudioFiles(env));
        Assert.Null(await env.Stores.Transcripts.GetAsync(original));
        Assert.True((await env.Stores.Audit.VerifyAsync()).Ok);
        var view = (await env.Meeting.ListDeleteRequestsAsync(env.Carol)).Single();
        Assert.Equal("Deleted", view.Outcome);
        Assert.False(view.Open);
        await Assert.ThrowsAsync<ForbiddenException>(() => env.Meeting.ListDeleteRequestsAsync(env.Alice));
    }

    [Fact]
    public async Task An_admin_who_asks_counts_as_the_first_of_the_two()
    {
        var (env, _, reopened) = await ReopenedAsync();
        using var _e = env;
        var req = await env.Meeting.RequestDeleteAsync(env.Carol, reopened, "Duplicate");
        await Assert.ThrowsAsync<DomainException>(() => env.Meeting.ApproveDeleteAsync(env.Carol, req));
        Assert.True(await env.Meeting.ApproveDeleteAsync(env.Dave, req));
        Assert.Equal(ItemStatus.Purged, (await env.Item(reopened)).Status);
    }

    [Fact]
    public async Task A_rejected_request_deletes_nothing()
    {
        var (env, original, reopened) = await ReopenedAsync();
        using var _ = env;
        var req = await env.Meeting.RequestDeleteAsync(env.Alice, reopened, "Not needed");
        await env.Meeting.RejectDeleteAsync(env.Carol, req, "Keep it for the client");
        await Assert.ThrowsAsync<DomainException>(() => env.Meeting.ApproveDeleteAsync(env.Dave, req));
        Assert.Equal(ItemStatus.Completed, (await env.Item(original)).Status);
        Assert.NotEmpty(AudioFiles(env));
    }

    [Fact]
    public async Task The_database_lets_an_approved_item_change_only_to_deleted_and_a_deleted_item_never_changes()
    {
        using var env = new Env();
        var id = await env.ProcessedRecordingAsync();
        await env.Meeting.ApproveAsync(env.Alice, id);
        Assert.Throws<SqliteException>(() => env.Execute($"UPDATE doc_items SET json = json_set(json, '$.name', 'changed') WHERE id = '{id}'"));
        env.Execute($"UPDATE doc_items SET json = json_set(json, '$.status', 'Purged') WHERE id = '{id}'");
        Assert.Throws<SqliteException>(() => env.Execute($"UPDATE doc_items SET json = json_set(json, '$.status', 'WithAuthor') WHERE id = '{id}'"));
        Assert.Throws<SqliteException>(() => env.Execute($"DELETE FROM doc_items WHERE id = '{id}'"));
    }
}
