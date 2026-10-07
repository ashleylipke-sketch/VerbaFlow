using VerbaFlow.Core.Domain;
using VerbaFlow.Infrastructure.Services;

namespace VerbaFlow.Tests.Services;

public class ReopenServiceTests
{
    private static async Task<(Env env, Guid id)> ApprovedAsync()
    {
        var env = new Env();
        var id = await env.ProcessedRecordingAsync();
        var seg = (await env.Meeting.GetTranscriptAsync(env.Alice, id)).Segments[0];
        await env.Meeting.EditSegmentAsync(env.Alice, id, seg.Id, "Edited before approval");
        await env.Meeting.ApproveAsync(env.Alice, id);
        return (env, id);
    }

    [Fact]
    public async Task Two_different_admins_create_a_linked_new_version_and_the_original_is_untouched()
    {
        var (env, id) = await ApprovedAsync();
        using var _ = env;
        var req = await env.Meeting.RequestReopenAsync(env.Alice, id, "Client name was wrong");
        Assert.Null(await env.Meeting.ApproveReopenAsync(env.Carol, req));
        var newId = await env.Meeting.ApproveReopenAsync(env.Dave, req);
        Assert.NotNull(newId);

        var original = await env.Item(id);
        Assert.Equal(ItemStatus.Completed, original.Status);        // still locked and archived
        var next = await env.Item(newId!.Value);
        Assert.Equal(2, next.VersionNo);
        Assert.Equal(id, next.SupersedesItemId);
        Assert.Equal(ItemStatus.WithAuthor, next.Status);

        var t = await env.Meeting.GetTranscriptAsync(env.Alice, newId.Value);
        Assert.Equal("MachineV1", t.Versions[0].Kind);               // history preserved
        Assert.Equal("ReopenCopy", t.Versions[^1].Kind);
        Assert.Equal("Edited before approval", t.Segments[0].Text);
        var detail = await env.Meeting.GetItemAsync(env.Alice, newId.Value);
        Assert.Equal(2, detail.Chain.Count);
        Assert.Contains("v2", detail.Row.Tags);
        Assert.Contains("Superseded", (await env.Meeting.GetItemAsync(env.Alice, id)).Row.Tags);
        await using var audio = await env.Meeting.OpenAudioAsync(env.Alice, newId.Value, false);   // same original audio
        Assert.NotNull(audio.Content);
    }

    [Fact]
    public async Task The_new_version_has_its_own_trail_that_starts_with_the_reopen_and_the_original_gets_one_pointer()
    {
        var (env, id) = await ApprovedAsync();
        using var _ = env;
        var req = await env.Meeting.RequestReopenAsync(env.Alice, id, "Client name was wrong");
        await env.Meeting.ApproveReopenAsync(env.Carol, req);
        var newId = (await env.Meeting.ApproveReopenAsync(env.Dave, req))!.Value;

        var v2 = await env.Meeting.GetAuditAsync(env.Alice, newId);
        Assert.Equal("item.reopened", v2[0].Type);
        Assert.Contains("Client name was wrong", v2[0].Details);
        var original = await env.Meeting.GetAuditAsync(env.Alice, id);
        Assert.Equal(1, original.Count(e => e.Type == "item.reopened_version_created"));
        Assert.True((await env.Meeting.VerifyAuditAsync(env.Carol)).Ok);
    }

    [Fact]
    public async Task The_requester_cannot_be_one_of_the_two_approvers_even_if_they_are_an_admin()
    {
        var (env, id) = await ApprovedAsync();
        using var _ = env;
        var req = await env.Meeting.RequestReopenAsync(env.Carol, id, "Needs correcting");
        await Assert.ThrowsAsync<DomainException>(() => env.Meeting.ApproveReopenAsync(env.Carol, req));
        await env.Meeting.ApproveReopenAsync(env.Dave, req);
        await Assert.ThrowsAsync<DomainException>(() => env.Meeting.ApproveReopenAsync(env.Dave, req));
    }

    [Fact]
    public async Task A_reopen_needs_a_reason_and_non_admins_cannot_approve()
    {
        var (env, id) = await ApprovedAsync();
        using var _ = env;
        await Assert.ThrowsAsync<DomainException>(() => env.Meeting.RequestReopenAsync(env.Alice, id, " "));
        await Assert.ThrowsAsync<ForbiddenException>(() => env.Meeting.RequestReopenAsync(env.Xavier, id, "Please"));
        var req = await env.Meeting.RequestReopenAsync(env.Alice, id, "Typo");
        await Assert.ThrowsAsync<DomainException>(() => env.Meeting.ApproveReopenAsync(env.Bob, req));
    }

    [Fact]
    public async Task A_rejected_request_creates_nothing_and_a_version_cannot_be_reopened_twice()
    {
        var (env, id) = await ApprovedAsync();
        using var _ = env;
        var req = await env.Meeting.RequestReopenAsync(env.Alice, id, "Maybe");
        await env.Meeting.RejectReopenAsync(env.Carol, req, "Not justified");
        Assert.Single(await env.Stores.Items.ListAsync());
        var req2 = await env.Meeting.RequestReopenAsync(env.Alice, id, "Really needed");
        await env.Meeting.ApproveReopenAsync(env.Carol, req2);
        await env.Meeting.ApproveReopenAsync(env.Dave, req2);
        await Assert.ThrowsAsync<DomainException>(() => env.Meeting.RequestReopenAsync(env.Alice, id, "Again"));
    }

    [Fact]
    public async Task Only_completed_items_can_have_a_reopen_requested()
    {
        using var env = new Env();
        var id = await env.ProcessedRecordingAsync();
        await Assert.ThrowsAsync<DomainException>(() => env.Meeting.RequestReopenAsync(env.Alice, id, "Too early"));
    }
}
