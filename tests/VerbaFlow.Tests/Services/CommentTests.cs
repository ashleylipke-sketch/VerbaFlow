using VerbaFlow.Core.Domain;
using VerbaFlow.Core.Providers;
using VerbaFlow.Core.Transcripts;
using VerbaFlow.Infrastructure.Services;

namespace VerbaFlow.Tests.Services;

public class CommentTests
{
    private sealed class NoMail : IEmailSender
    {
        public string Name => "none";
        public Task<string> SendAsync(EmailMessage message, CancellationToken ct) => Task.FromResult("ref");
    }

    [Fact]
    public async Task A_comment_sits_on_its_words_follows_them_through_edits_and_gets_replies_and_resolved()
    {
        using var env = new Env();
        var comments = new CommentService(env.Stores, env.Clock);
        var bell = new NotificationService(env.Stores, new NoMail(), env.Clock, env.Reporter, "https://verbaflow.example");
        await bell.ProcessAsync();
        env.Speech.Heard = "Please call Mr Smith about the contract full stop";
        var id = await env.Meeting.CreateRecordingAsync(env.Alice, env.Dictation());
        await env.Processing.ProcessAsync(id);
        await env.Meeting.AssignAsync(env.Alice, id, env.Bob.Id, null);
        var seg = (await env.Meeting.GetTranscriptAsync(env.Alice, id)).Segments.Single(s => s.Text.Length > 0);
        Assert.Equal("Please call Mr Smith about the contract.", seg.Text);

        // Alice comments on "Mr Smith"; outsiders can neither read nor write; bad selections are refused plainly.
        var list = await comments.AddAsync(env.Alice, id, seg.Id, 2, 4, "Is it Mr Smith or Mrs Smith?");
        var c = Assert.Single(list.Comments);
        Assert.Equal("Mr Smith", c.Quote);
        Assert.Equal(new CommentPlace(seg.Id, 2, 4), c.Place);
        await Assert.ThrowsAsync<ForbiddenException>(() => comments.ListAsync(env.Xavier, id));
        await Assert.ThrowsAsync<DomainException>(() => comments.AddAsync(env.Alice, id, seg.Id, 3, 3, "nothing selected"));
        await Assert.ThrowsAsync<DomainException>(() => comments.AddAsync(env.Alice, id, seg.Id, 0, 99, "too far"));
        await Assert.ThrowsAsync<DomainException>(() => comments.AddAsync(env.Alice, id, Guid.NewGuid(), 0, 1, "gone"));

        // Bob (assigned, not yet accepted) replies; Alice is told, and Bob was told about the comment.
        await comments.ReplyAsync(env.Bob, id, c.Id, "Mr, he said so at the start.");
        await bell.ProcessAsync();
        Assert.Contains("Alice commented on “Letter to the client”.", (await bell.ListAsync(env.Bob)).Items.Select(i => i.Text));
        Assert.Contains("Bob replied to a comment on “Letter to the client”.", (await bell.ListAsync(env.Alice)).Items.Select(i => i.Text));
        Assert.Contains((await bell.ListAsync(env.Bob)).Items, i => i.Link.EndsWith($"?comment={c.Id}"));

        // An edit elsewhere in the passage gives it a new id; the comment moves with its words.
        await env.Meeting.EditSegmentAsync(env.Alice, id, seg.Id, "Please do call Mr Smith about the contract.");
        var moved = (await comments.ListAsync(env.Alice, id)).Comments.Single();
        Assert.NotEqual(seg.Id, moved.Place!.SegmentId);
        Assert.Equal((3, 5), (moved.Place.From, moved.Place.To));
        Assert.Equal("Mr, he said so at the start.", Assert.Single(moved.Replies).Text);

        // Resolve, and a later reply opens it again. The lists count open comments.
        list = await comments.SetResolvedAsync(env.Bob, id, c.Id, true);
        Assert.True(list.Comments[0].Resolved);
        Assert.Equal("Bob", list.Comments[0].ResolvedBy);
        Assert.Equal(0, (await env.Meeting.ListAsync(env.Alice, new ListQuery(null, null, null))).Single(r => r.Id == id).OpenComments);
        list = await comments.ReplyAsync(env.Alice, id, c.Id, "Thanks.");
        Assert.False(list.Comments[0].Resolved);
        Assert.Equal(1, (await env.Meeting.ListAsync(env.Alice, new ListQuery(null, null, null))).Single(r => r.Id == id).OpenComments);

        // When the words themselves are changed, the comment stays listed without a place.
        var now = (await env.Meeting.GetTranscriptAsync(env.Alice, id)).Segments.Single(s => s.Text.Length > 0);
        await env.Meeting.EditSegmentAsync(env.Alice, id, now.Id, "Please do call Mrs Jones about the contract.");
        Assert.Null((await comments.ListAsync(env.Alice, id)).Comments.Single().Place);

        // Only the writer removes it; the History has the steps but never the words.
        await Assert.ThrowsAsync<ForbiddenException>(() => comments.RemoveAsync(env.Bob, id, c.Id));
        var history = await env.Stores.Audit.ListAsync(id);
        Assert.Contains(history, a => a.Type == "comment.added");
        Assert.Contains(history, a => a.Type == "comment.replied");
        Assert.Contains(history, a => a.Type == "comment.resolved");
        Assert.DoesNotContain(history, a => a.Details.Contains("Mrs Smith") || a.Details.Contains("he said so"));
        var latest = (await env.Meeting.GetTranscriptAsync(env.Alice, id)).Segments.Single(s => s.Text.Length > 0);
        list = await comments.AddAsync(env.Alice, id, latest.Id, 0, 1, "Remove me");
        list = await comments.RemoveAsync(env.Alice, id, list.Comments.Single(x => x.Text == "Remove me").Id);
        Assert.Single(list.Comments);

        // Approval locks them; deleting the recording deletes them.
        await env.Meeting.AcceptAsync(env.Bob, id);
        await env.Meeting.ReturnAsync(env.Bob, id);
        await env.Meeting.ApproveAsync(env.Alice, id);
        Assert.False((await comments.ListAsync(env.Alice, id)).CanAdd);
        await Assert.ThrowsAsync<DomainException>(() => comments.ReplyAsync(env.Alice, id, c.Id, "Too late"));

        var other = await env.ProcessedRecordingAsync();
        var otherSeg = (await env.Meeting.GetTranscriptAsync(env.Alice, other)).Segments.First(s => s.Text.Length > 0);
        await comments.AddAsync(env.Alice, other, otherSeg.Id, 0, 1, "Delete me with it");
        await env.Meeting.DeleteAsync(env.Alice, other, "test");
        Assert.DoesNotContain(await env.Stores.Comments.ListAsync(), x => x.Text == "Delete me with it");
    }

    [Fact]
    public void Of_several_matches_the_one_nearest_the_original_place_is_taken_and_punctuation_does_not_matter()
    {
        var a = new Segment(Guid.NewGuid(), Guid.NewGuid(), "en", 0, 1000, "The fee, the fee. And the FEE!", 1, false);
        Assert.Equal(new CommentPlace(a.Id, 5, 7), CommentAnchors.Locate([a], Guid.NewGuid(), 0, 4, "the fee"));
        Assert.Equal(new CommentPlace(a.Id, 0, 2), CommentAnchors.Locate([a], a.Id, 0, 0, "The fee,"));
        var b = new Segment(Guid.NewGuid(), Guid.NewGuid(), "en", 5000, 6000, "Another fee.", 1, false);
        Assert.Equal(b.Id, CommentAnchors.Locate([a, b], Guid.NewGuid(), 5000, 1, "fee")!.SegmentId); // same start time first
        Assert.Null(CommentAnchors.Locate([a, b], a.Id, 0, 0, "the price"));
    }
}
