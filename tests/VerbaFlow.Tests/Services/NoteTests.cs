using VerbaFlow.Core.Domain;
using VerbaFlow.Core.Providers;
using VerbaFlow.Infrastructure.Services;

namespace VerbaFlow.Tests.Services;

public class NoteTests
{
    private sealed class NoMail : IEmailSender
    {
        public string Name => "none";
        public Task<string> SendAsync(EmailMessage message, CancellationToken ct) => Task.FromResult("ref");
    }

    [Fact]
    public async Task The_owner_and_the_typist_leave_notes_tick_them_off_and_are_told_about_each_others()
    {
        using var env = new Env();
        var notes = new NoteService(env.Stores, env.Clock);
        var bell = new NotificationService(env.Stores, new NoMail(), env.Clock, env.Reporter, "https://verbaflow.example");
        await bell.ProcessAsync();
        var id = await env.ProcessedRecordingAsync();
        await env.Meeting.AssignAsync(env.Alice, id, env.Bob.Id, null);

        // Bob may ask before accepting; Xavier (not working on it) may read nothing and write nothing.
        var list = await notes.AddAsync(env.Alice, id, "  Check the client's name.\r\n\r\n\r\nIt is Reiss.  ");
        Assert.Equal("Check the client's name.\n\nIt is Reiss.", Assert.Single(list.Notes).Text);
        await notes.AddAsync(env.Bob, id, "Done by Friday?");
        await Assert.ThrowsAsync<ForbiddenException>(() => notes.ListAsync(env.Xavier, id));
        await Assert.ThrowsAsync<DomainException>(() => notes.AddAsync(env.Alice, id, "   "));
        await Assert.ThrowsAsync<DomainException>(() => notes.AddAsync(env.Alice, id, new string('x', ItemNote.MaxLength + 1)));

        await bell.ProcessAsync();
        Assert.Contains("Alice left a note on “Board meeting”.", (await bell.ListAsync(env.Bob)).Items.Select(i => i.Text));
        Assert.Contains("Bob left a note on “Board meeting”.", (await bell.ListAsync(env.Alice)).Items.Select(i => i.Text));
        Assert.DoesNotContain(await bell.ListAsync(env.Alice).ContinueWith(t => t.Result.Items), i => i.Text.StartsWith("Alice left"));

        // Only the writer removes a note; anyone working on it ticks it off. The list shows open notes.
        var first = (await notes.ListAsync(env.Bob, id)).Notes[0];
        Assert.False(first.CanRemove);
        await Assert.ThrowsAsync<ForbiddenException>(() => notes.RemoveAsync(env.Bob, id, first.Id));
        Assert.Equal(2, (await env.Meeting.ListAsync(env.Alice, new ListQuery(null, null, null))).Single(r => r.Id == id).OpenNotes);
        list = await notes.SetDoneAsync(env.Bob, id, first.Id, true);
        Assert.True(list.Notes[0].Done);
        Assert.Equal("Bob", list.Notes[0].DoneBy);
        Assert.Equal(1, (await env.Meeting.ListAsync(env.Alice, new ListQuery(null, null, null))).Single(r => r.Id == id).OpenNotes);
        list = await notes.RemoveAsync(env.Bob, id, list.Notes[1].Id);
        Assert.Single(list.Notes);

        var history = (await env.Stores.Audit.ListAsync(id)).Select(a => a.Type).ToList();
        Assert.Equal(2, history.Count(t => t == "note.added"));
        Assert.Contains("note.done", history);
        Assert.Contains("note.removed", history);
        Assert.DoesNotContain((await env.Stores.Audit.ListAsync(id)), a => a.Details.Contains("Reiss")); // the words stay out of the History

        // Approval locks them; deleting the recording deletes them.
        await env.Meeting.AcceptAsync(env.Bob, id);
        await env.Meeting.ReturnAsync(env.Bob, id);
        await env.Meeting.ApproveAsync(env.Alice, id);
        var locked = await notes.ListAsync(env.Alice, id);
        Assert.False(locked.CanAdd);
        await Assert.ThrowsAsync<DomainException>(() => notes.AddAsync(env.Alice, id, "Too late"));

        var other = await env.ProcessedRecordingAsync();
        await notes.AddAsync(env.Alice, other, "Delete me with it");
        await env.Meeting.DeleteAsync(env.Alice, other, "test");
        Assert.DoesNotContain(await env.Stores.Notes.ListAsync(), n => n.Text == "Delete me with it");
    }
}
