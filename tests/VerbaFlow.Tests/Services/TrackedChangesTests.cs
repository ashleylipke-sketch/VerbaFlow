using VerbaFlow.Core.Domain;

namespace VerbaFlow.Tests.Services;

public class TrackedChangesTests
{
    [Fact]
    public async Task Untouched_passages_carry_no_original_and_edited_ones_carry_the_machine_text()
    {
        using var env = new Env();
        var id = await env.ProcessedRecordingAsync();
        var before = await env.Meeting.GetTranscriptAsync(env.Alice, id);
        Assert.All(before.Segments, s => Assert.Null(s.OriginalText));

        var machineText = before.Segments[1].Text;
        await env.Meeting.EditSegmentAsync(env.Alice, id, before.Segments[1].Id, "A corrected line.");
        var after = await env.Meeting.GetTranscriptAsync(env.Alice, id);
        Assert.Equal("A corrected line.", after.Segments[1].Text);
        Assert.Equal(machineText, after.Segments[1].OriginalText);
        Assert.Null(after.Segments[0].OriginalText);
    }

    [Fact]
    public async Task The_original_is_always_the_machine_text_even_after_several_edits()
    {
        using var env = new Env();
        var id = await env.ProcessedRecordingAsync();
        var t0 = await env.Meeting.GetTranscriptAsync(env.Alice, id);
        var machineText = t0.Segments[0].Text;
        await env.Meeting.EditSegmentAsync(env.Alice, id, t0.Segments[0].Id, "First edit.");
        var t1 = await env.Meeting.GetTranscriptAsync(env.Alice, id);
        await env.Meeting.EditSegmentAsync(env.Alice, id, t1.Segments[0].Id, "Second edit.");
        var t2 = await env.Meeting.GetTranscriptAsync(env.Alice, id);
        Assert.Equal("Second edit.", t2.Segments[0].Text);
        Assert.Equal(machineText, t2.Segments[0].OriginalText);
    }

    [Fact]
    public async Task Restoring_the_machine_version_clears_the_marks_and_an_older_version_can_still_be_compared()
    {
        using var env = new Env();
        var id = await env.ProcessedRecordingAsync();
        var t0 = await env.Meeting.GetTranscriptAsync(env.Alice, id);
        await env.Meeting.EditSegmentAsync(env.Alice, id, t0.Segments[0].Id, "Edited.");
        Assert.NotNull((await env.Meeting.GetTranscriptAsync(env.Alice, id, 2)).Segments[0].OriginalText);
        Assert.Null((await env.Meeting.GetTranscriptAsync(env.Alice, id, 1)).Segments[0].OriginalText);

        await env.Meeting.RestoreVersionAsync(env.Alice, id, 1);
        var restored = await env.Meeting.GetTranscriptAsync(env.Alice, id);
        Assert.All(restored.Segments, s => Assert.Null(s.OriginalText));
    }

    [Fact]
    public async Task A_reopened_copy_still_compares_against_the_original_machine_text()
    {
        using var env = new Env();
        var id = await env.ProcessedRecordingAsync();
        var t0 = await env.Meeting.GetTranscriptAsync(env.Alice, id);
        var machineText = t0.Segments[0].Text;
        await env.Meeting.EditSegmentAsync(env.Alice, id, t0.Segments[0].Id, "Approved wording.");
        await env.Meeting.ApproveAsync(env.Alice, id);
        var req = await env.Meeting.RequestReopenAsync(env.Alice, id, "Needs another look");
        await env.Meeting.ApproveReopenAsync(env.Carol, req);
        var newId = await env.Meeting.ApproveReopenAsync(env.Dave, req);
        var copy = await env.Meeting.GetTranscriptAsync(env.Alice, newId!.Value);
        Assert.Equal("Approved wording.", copy.Segments[0].Text);
        Assert.Equal(machineText, copy.Segments[0].OriginalText);
    }
}
