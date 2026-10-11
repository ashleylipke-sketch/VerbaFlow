using VerbaFlow.Core.Domain;
using VerbaFlow.Infrastructure.Services;

namespace VerbaFlow.Tests.Services;

public class SavedTextTests
{
    [Fact]
    public async Task Administrators_add_change_and_remove_saved_text_and_each_change_is_audited()
    {
        using var env = new Env();
        var t = await env.SavedText.AddAsync(env.Carol, "  legal   1,1 ", "First line.\r\n\r\n\r\nSecond line.  ");
        Assert.Equal("legal 1,1", t.Code);
        Assert.Equal("First line.\n\nSecond line.", t.Text);
        var changed = await env.SavedText.ChangeAsync(env.Dave, t.Id, "legal 1.1", "New wording.");
        Assert.Equal(env.Dave.Id, changed.ChangedBy);
        await env.SavedText.RemoveAsync(env.Carol, t.Id);
        Assert.Empty(await env.SavedText.ListAsync());
        var types = (await env.Stores.Audit.ListAsync(null)).Select(a => a.Type).ToList();
        Assert.Contains("saved-text.added", types);
        Assert.Contains("saved-text.changed", types);
        Assert.Contains("saved-text.removed", types);
    }

    [Fact]
    public async Task Only_administrators_change_it_and_codes_must_be_unique_however_written()
    {
        using var env = new Env();
        var t = await env.SavedText.AddAsync(env.Carol, "legal 1.1", "Text");
        await Assert.ThrowsAsync<ForbiddenException>(() => env.SavedText.AddAsync(env.Alice, "legal 2", "Text"));
        await Assert.ThrowsAsync<ForbiddenException>(() => env.SavedText.ChangeAsync(env.Alice, t.Id, "legal 2", "Text"));
        await Assert.ThrowsAsync<ForbiddenException>(() => env.SavedText.RemoveAsync(env.Alice, t.Id));
        await Assert.ThrowsAsync<DomainException>(() => env.SavedText.AddAsync(env.Carol, "Legal 1,1", "Other"));
        await Assert.ThrowsAsync<DomainException>(() => env.SavedText.AddAsync(env.Carol, "1.1", "No word first"));
        await Assert.ThrowsAsync<DomainException>(() => env.SavedText.AddAsync(env.Carol, "insert 1", "Starts with insert"));
        await Assert.ThrowsAsync<DomainException>(() => env.SavedText.AddAsync(env.Carol, "legal 3", "   "));
        Assert.Single(await env.SavedText.ListAsync());
    }

    [Fact]
    public async Task A_dictation_saying_insert_and_a_code_gets_the_saved_text_and_history_records_which_wording()
    {
        using var env = new Env();
        await env.SavedText.AddAsync(env.Carol, "legal 1,1", "This letter is confidential.");
        env.Speech.Heard = "Dear Ms Smith comma new paragraph. Insert legal 1.1. Thank you full stop";
        var id = await env.Meeting.CreateRecordingAsync(env.Alice, env.Dictation());
        await env.Processing.ProcessAsync(id);

        var text = (await env.Stores.Transcripts.GetAsync(id))!.Render().Select(t => t.Segment.Text).ToList();
        Assert.Equal(["Dear Ms Smith,", "This letter is confidential. Thank you."], text);
        Assert.Contains("insert legal 1,1", env.Speech.LastOptions!.Phrases!);
        var history = (await env.Stores.Audit.ListAsync(id)).Last().Details;
        Assert.Contains("\"savedTextInserted\":\"legal 1,1 (" + SavedTextService.Fingerprint("This letter is confidential.")[..12] + ")\"", history);
        Assert.Contains("insert legal 1,1 1", history);
    }
}
