using VerbaFlow.Core.Domain;
using VerbaFlow.Infrastructure.Services;

namespace VerbaFlow.Tests.Services;

public class UnfinishedDictationTests
{
    private static DraftUpload First(string audio = "first part") =>
        new(Env.Audio(audio), "dictation.wav", "audio/wav", 10_000, "Letter to Mr Smith", "en", "en-GB", "ACME-042", "letter");

    private static DraftUpload Next(string audio, int lengthMs = 20_000) => new(Env.Audio(audio), "dictation.wav", "audio/wav", lengthMs);

    private static async Task<string> Played(Env env, User who, Guid id)
    {
        await using var a = await env.Meeting.OpenAudioAsync(who, id, false);
        using var r = new StreamReader(a.Content);
        return await r.ReadToEndAsync();
    }

    [Fact]
    public async Task A_dictation_saved_unfinished_is_a_draft_that_is_not_converted_and_plays_its_newest_save()
    {
        using var env = new Env();
        var id = await env.Meeting.SaveDraftAsync(env.Alice, null, First());
        var item = await env.Item(id);
        Assert.Equal(ItemStatus.Draft, item.Status);
        Assert.Equal(ProcessingState.Idle, item.Processing);
        Assert.Equal("letter", item.DocumentType);
        Assert.Equal("ACME-042", item.ClientReference);
        Assert.Equal("first part", await Played(env, env.Alice, id));

        await env.Meeting.SaveDraftAsync(env.Alice, id, Next("first part and more"));
        Assert.Equal("first part and more", await Played(env, env.Alice, id));
        Assert.Equal(2, (await env.Item(id)).DraftVersion);
        Assert.Equal(20_000, (await env.Item(id)).LengthMs);
        Assert.Equal("Unfinished", (await env.Meeting.ListAsync(env.Alice, new ListQuery(null, null, null, ItemMode.Dictate))).Single().Status);
        Assert.Null(await env.Stores.Transcripts.GetAsync(id));
        var types = (await env.Stores.Audit.ListAsync(id)).Select(a => a.Type).ToList();
        Assert.Equal(["item.created", "draft.saved", "audio.played", "draft.saved", "audio.played"], types);
    }

    [Fact]
    public async Task Every_save_is_kept_as_its_own_file_and_earlier_saves_are_never_changed()
    {
        using var env = new Env();
        var id = await env.Meeting.SaveDraftAsync(env.Alice, null, First());
        await env.Meeting.SaveDraftAsync(env.Alice, id, Next("second"));
        var assets = (await env.Stores.MediaAssets.ListAsync()).Where(a => a.ItemId == id).OrderBy(a => a.Kind).ToList();
        Assert.Equal(["draft:1", "draft:2"], assets.Select(a => a.Kind));
        Assert.True(await env.Stores.Media.VerifyAsync(assets[0]));
    }

    [Fact]
    public async Task Finishing_makes_the_final_audio_the_recording_and_converts_it()
    {
        using var env = new Env();
        env.Speech.Heard = "Dear Mr Smith comma thank you full stop";
        var id = await env.Meeting.SaveDraftAsync(env.Alice, null, First());
        await env.Meeting.FinishDraftAsync(env.Alice, id, Next("the whole dictation", 45_000));
        var item = await env.Item(id);
        Assert.Equal(ItemStatus.WithAuthor, item.Status);
        Assert.Equal(ProcessingState.Running, item.Processing);
        Assert.Equal(45_000, item.LengthMs);
        Assert.Equal("the whole dictation", await Played(env, env.Alice, id));

        await env.Processing.ProcessAsync(id);
        Assert.Equal(["Dear Mr Smith, thank you."], (await env.Stores.Transcripts.GetAsync(id))!.Render().Select(t => t.Segment.Text));
        await Assert.ThrowsAsync<DomainException>(() => env.Meeting.SaveDraftAsync(env.Alice, id, Next("too late")));
        await Assert.ThrowsAsync<DomainException>(() => env.Meeting.FinishDraftAsync(env.Alice, id, Next("too late")));
    }

    [Fact]
    public async Task Only_the_author_can_carry_on_and_a_draft_needs_a_name_and_type()
    {
        using var env = new Env();
        var id = await env.Meeting.SaveDraftAsync(env.Alice, null, First());
        await Assert.ThrowsAsync<ForbiddenException>(() => env.Meeting.SaveDraftAsync(env.Bob, id, Next("not mine")));
        await Assert.ThrowsAsync<ForbiddenException>(() => env.Meeting.SaveDraftAsync(env.Carol, id, Next("admin, still not mine")));
        await Assert.ThrowsAsync<ForbiddenException>(() => env.Meeting.FinishDraftAsync(env.Bob, id, Next("not mine")));
        await Assert.ThrowsAsync<DomainException>(() => env.Meeting.SaveDraftAsync(env.Alice, null, First() with { Name = " " }));
        await Assert.ThrowsAsync<DomainException>(() => env.Meeting.SaveDraftAsync(env.Alice, null, First() with { DocumentType = null }));
        await Assert.ThrowsAsync<DomainException>(() => env.Meeting.AssignAsync(env.Alice, id, env.Bob.Id, null));
        var mine = (await env.Meeting.GetItemAsync(env.Alice, id)).Row.Actions;
        Assert.Contains("continue", mine);
        Assert.DoesNotContain("rerun", mine);
        Assert.DoesNotContain("approve", mine);
        Assert.DoesNotContain("continue", (await env.Meeting.GetItemAsync(env.Carol, id)).Row.Actions);
    }

    [Fact]
    public async Task A_draft_can_be_deleted_and_its_saved_audio_goes_with_it()
    {
        using var env = new Env();
        var id = await env.Meeting.SaveDraftAsync(env.Alice, null, First());
        await env.Meeting.SaveDraftAsync(env.Alice, id, Next("second"));
        await env.Meeting.DeleteAsync(env.Alice, id, "Started again");
        Assert.Equal(ItemStatus.Purged, (await env.Item(id)).Status);
        await Assert.ThrowsAsync<NotFoundException>(() => env.Meeting.OpenAudioAsync(env.Alice, id, false));
    }
}
