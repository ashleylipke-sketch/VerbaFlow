using VerbaFlow.Core.Domain;
using VerbaFlow.Infrastructure.Services;

namespace VerbaFlow.Tests.Services;

public class ChangeRecordingTests
{
    private static DraftUpload Changed(string audio = "changed recording", int lengthMs = 40_000) =>
        new(Env.Audio(audio), "dictation.wav", "audio/wav", lengthMs);

    private static async Task<string> Played(Env env, Guid id)
    {
        await using var a = await env.Meeting.OpenAudioAsync(env.Alice, id, false);
        using var r = new StreamReader(a.Content);
        return await r.ReadToEndAsync();
    }

    private static async Task<Guid> ConvertedDictation(Env env)
    {
        env.Speech.Heard = "Dear Ms Smith full stop";
        var id = await env.Meeting.CreateRecordingAsync(env.Alice, env.Dictation());
        await env.Processing.ProcessAsync(id);
        return id;
    }

    [Fact]
    public async Task The_author_changes_the_recording_before_approval_and_it_is_converted_again()
    {
        using var env = new Env();
        var id = await ConvertedDictation(env);
        Assert.Contains("change-recording", (await env.Meeting.GetItemAsync(env.Alice, id)).Row.Actions);
        var first = (await env.Stores.Media.FindOriginalAsync(id))!;

        await env.Meeting.ChangeRecordingAsync(env.Alice, id, Changed(), false);
        var item = await env.Item(id);
        Assert.Equal(ProcessingState.Running, item.Processing);
        Assert.Equal(40_000, item.LengthMs);
        Assert.Equal(1, item.RecordingRevision);
        Assert.Equal("changed recording", await Played(env, id));
        Assert.True(await env.Stores.Media.VerifyAsync(first)); // the first recording is kept unchanged

        env.Speech.Heard = "Dear Ms Smith comma thank you full stop";
        await env.Processing.ProcessAsync(id);
        Assert.Equal(["Dear Ms Smith, thank you."], (await env.Stores.Transcripts.GetAsync(id))!.Render().Select(t => t.Segment.Text));
        var changed = (await env.Stores.Audit.ListAsync(id)).Single(a => a.Type == "recording.changed");
        Assert.Contains(first.Sha256, changed.Details);

        await env.Meeting.ApproveAsync(env.Alice, id);
        Assert.DoesNotContain(first.Sha256, (await env.Stores.Audit.ListAsync(id)).Last().Details); // approval records the changed recording
        await Assert.ThrowsAsync<DomainException>(() => env.Meeting.ChangeRecordingAsync(env.Alice, id, Changed(), false));
    }

    [Fact]
    public async Task Saved_corrections_are_only_replaced_when_the_author_agrees()
    {
        using var env = new Env();
        var id = await ConvertedDictation(env);
        var seg = (await env.Stores.Transcripts.GetAsync(id))!.Render().First().Segment;
        await env.Meeting.EditSegmentAsync(env.Alice, id, seg.Id, "Dear Mrs Smith.");
        await Assert.ThrowsAsync<DomainException>(() => env.Meeting.ChangeRecordingAsync(env.Alice, id, Changed(), false));
        await env.Meeting.ChangeRecordingAsync(env.Alice, id, Changed(), true);
        Assert.Contains("\"replacedEdits\":1", (await env.Stores.Audit.ListAsync(id)).Single(a => a.Type == "recording.changed").Details);
    }

    [Fact]
    public async Task Only_the_author_while_it_is_with_them_and_only_for_a_dictation()
    {
        using var env = new Env();
        var id = await ConvertedDictation(env);
        await Assert.ThrowsAsync<ForbiddenException>(() => env.Meeting.ChangeRecordingAsync(env.Carol, id, Changed(), false));
        await env.Meeting.AssignAsync(env.Alice, id, env.Bob.Id, null);
        Assert.DoesNotContain("change-recording", (await env.Meeting.GetItemAsync(env.Alice, id)).Row.Actions);
        await Assert.ThrowsAsync<DomainException>(() => env.Meeting.ChangeRecordingAsync(env.Alice, id, Changed(), false));

        var meeting = await env.ProcessedRecordingAsync();
        await Assert.ThrowsAsync<DomainException>(() => env.Meeting.ChangeRecordingAsync(env.Alice, meeting, Changed(), false));
    }

    [Fact]
    public async Task A_reopened_dictation_can_be_changed_without_touching_the_approved_version()
    {
        using var env = new Env();
        var v1 = await ConvertedDictation(env);
        await env.Meeting.ApproveAsync(env.Alice, v1);
        var req = await env.Meeting.RequestReopenAsync(env.Alice, v1, "Add a paragraph");
        await env.Meeting.ApproveReopenAsync(env.Carol, req);
        var v2 = (await env.Meeting.ApproveReopenAsync(env.Dave, req))!.Value;
        await env.Processing.ProcessAsync(v2);
        await env.Meeting.ChangeRecordingAsync(env.Alice, v2, Changed("version two"), true);
        Assert.Equal("version two", await Played(env, v2));
        Assert.Equal("pretend this is audio", await Played(env, v1));
    }
}
