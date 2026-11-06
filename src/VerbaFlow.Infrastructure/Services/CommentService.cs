using System.Security.Cryptography;
using System.Text;
using VerbaFlow.Core.Audit;
using VerbaFlow.Core.Domain;
using VerbaFlow.Core.Security;
using VerbaFlow.Core.Transcripts;

namespace VerbaFlow.Infrastructure.Services;

public sealed record ReplyRow(Guid Id, string Text, string By, DateTimeOffset At);

/// <param name="Place">Where the words are in the text now, or null when they were changed or taken out.</param>
public sealed record CommentRow(Guid Id, string Quote, string Text, string By, Guid ById, DateTimeOffset At, int AtMs,
    CommentPlace? Place, IReadOnlyList<ReplyRow> Replies, bool Resolved, string? ResolvedBy, DateTimeOffset? ResolvedAt, bool CanRemove);

/// <param name="CanAdd">Whether this person can comment, reply and resolve now.</param>
/// <param name="Why">A plain sentence when they cannot, or null.</param>
public sealed record CommentList(IReadOnlyList<CommentRow> Comments, bool CanAdd, string? Why);

/// <summary>
/// Comments on words of a transcript or dictation, with replies, for the people working on a recording: the owner, the
/// person it is assigned to, and administrators or department admins who look after it (the same people as notes).
/// Anyone who can open the recording reads them. A comment can be resolved (and opened again); only its writer can take it
/// away. Approval locks them. The History records each step with a fingerprint of the words, never the words themselves.
/// </summary>
public sealed class CommentService(Stores stores, TimeProvider clock)
{
    private const string Product = "speak";

    public async Task<CommentList> ListAsync(User actor, Guid itemId)
    {
        var item = await stores.RequireItemAsync(itemId);
        PermissionEvaluator.Require(actor, item, Capability.Read);
        var why = await WhyNotAsync(actor, item);
        var users = (await stores.Users.ListAsync()).ToDictionary(u => u.Id, u => u.Name);
        string Name(Guid id) => users.GetValueOrDefault(id, "?");
        var current = item.Status == ItemStatus.Purged ? [] : await CurrentAsync(item.Id);
        var rows = item.Status == ItemStatus.Purged ? [] : (await ForChainAsync(item.ChainId)).Select(c => new CommentRow(c.Id, c.Quote, c.Text,
                Name(c.AuthorId), c.AuthorId, c.At, c.AtMs, CommentAnchors.Locate(current, c.SegmentId, c.SegmentStartMs, c.From, c.Quote),
                c.Replies.Select(r => new ReplyRow(r.Id, r.Text, Name(r.AuthorId), r.At)).ToList(),
                c.Resolved, c.ResolvedBy is { } b ? Name(b) : null, c.ResolvedAt, why is null && c.AuthorId == actor.Id))
            .ToList();
        return new(rows, why is null, why);
    }

    public async Task<IReadOnlyList<TextComment>> ForChainAsync(Guid chainId) =>
        (await stores.Comments.ListAsync()).Where(c => c.ChainId == chainId).OrderBy(c => c.At).ToList();

    /// <summary>A comment on words [from, to) of a passage of the current text.</summary>
    public async Task<CommentList> AddAsync(User actor, Guid itemId, Guid segmentId, int from, int to, string? text)
    {
        var item = await stores.RequireItemAsync(itemId);
        PermissionEvaluator.Require(actor, item, Capability.Read);
        if (await WhyNotAsync(actor, item) is { } why) throw new DomainException(why);
        var t = Check(text, "comment");
        if ((await ForChainAsync(item.ChainId)).Count >= TextComment.MaxPerItem)
            throw new DomainException($"A recording can have up to {TextComment.MaxPerItem} comments. Remove some that are resolved first.");
        var seg = (await CurrentAsync(item.Id)).FirstOrDefault(s => s.Id == segmentId)
            ?? throw new DomainException("That part of the text has changed. Select the words again.");
        var words = CommentAnchors.Words(seg.Text);
        if (from < 0 || to > words.Length || from >= to) throw new DomainException("Select the words to comment on first.");
        if (to - from > TextComment.MaxWords) throw new DomainException($"A comment can be on up to {TextComment.MaxWords} words. Select fewer words.");
        var atMs = seg.Words is { } timed && timed.Count == words.Length ? timed[from].StartMs
            : seg.StartMs + (int)((long)(seg.EndMs - seg.StartMs) * from / Math.Max(1, words.Length));
        var c = new TextComment(Guid.NewGuid(), item.ChainId, item.Id, seg.Id, seg.StartMs, from, string.Join(' ', words[from..to]), atMs,
            actor.Id, t, clock.GetUtcNow(), []);
        await stores.Comments.UpsertAsync(c.Id, c);
        await Audit(actor, item, "comment.added", ("comment", c.Id), ("segmentId", seg.Id), ("words", to - from), ("characters", t.Length), ("sha256", Sha(t)));
        return await ListAsync(actor, itemId);
    }

    public async Task<CommentList> ReplyAsync(User actor, Guid itemId, Guid commentId, string? text)
    {
        var item = await stores.RequireItemAsync(itemId);
        PermissionEvaluator.Require(actor, item, Capability.Read);
        var c = await RequireAsync(item, commentId);
        if (await WhyNotAsync(actor, item) is { } why) throw new DomainException(why);
        var t = Check(text, "reply");
        if (c.Replies.Count >= TextComment.MaxReplies) throw new DomainException($"A comment can have up to {TextComment.MaxReplies} replies.");
        var r = new CommentReply(Guid.NewGuid(), actor.Id, t, clock.GetUtcNow());
        // A reply opens a resolved comment again: something still needs saying about it.
        c = c with { Replies = [.. c.Replies, r], ResolvedBy = null, ResolvedAt = null };
        await stores.Comments.UpsertAsync(c.Id, c);
        await Audit(actor, item, "comment.replied", ("comment", c.Id), ("reply", r.Id), ("characters", t.Length), ("sha256", Sha(t)));
        return await ListAsync(actor, itemId);
    }

    public async Task<CommentList> SetResolvedAsync(User actor, Guid itemId, Guid commentId, bool resolved)
    {
        var item = await stores.RequireItemAsync(itemId);
        PermissionEvaluator.Require(actor, item, Capability.Read);
        var c = await RequireAsync(item, commentId);
        if (await WhyNotAsync(actor, item) is { } why) throw new DomainException(why);
        if (c.Resolved == resolved) return await ListAsync(actor, itemId);
        c = resolved ? c with { ResolvedBy = actor.Id, ResolvedAt = clock.GetUtcNow() } : c with { ResolvedBy = null, ResolvedAt = null };
        await stores.Comments.UpsertAsync(c.Id, c);
        await Audit(actor, item, resolved ? "comment.resolved" : "comment.reopened", ("comment", c.Id));
        return await ListAsync(actor, itemId);
    }

    public async Task<CommentList> RemoveAsync(User actor, Guid itemId, Guid commentId)
    {
        var item = await stores.RequireItemAsync(itemId);
        PermissionEvaluator.Require(actor, item, Capability.Read);
        var c = await RequireAsync(item, commentId);
        if (await WhyNotAsync(actor, item) is { } why) throw new DomainException(why);
        if (c.AuthorId != actor.Id) throw new ForbiddenException("Only the person who wrote a comment can remove it. You can resolve it instead.");
        await stores.Comments.DeleteAsync(c.Id);
        await Audit(actor, item, "comment.removed", ("comment", c.Id), ("replies", c.Replies.Count), ("sha256", Sha(c.Text)));
        return await ListAsync(actor, itemId);
    }

    /// <summary>Comments not yet resolved per recording, by chain, for the lists.</summary>
    public static async Task<Dictionary<Guid, int>> OpenCountsAsync(Stores stores) =>
        (await stores.Comments.ListAsync()).Where(c => !c.Resolved).GroupBy(c => c.ChainId).ToDictionary(g => g.Key, g => g.Count());

    /// <summary>Deletes every comment of a recording when the recording itself is deleted. Returns how many there were.</summary>
    public static async Task<int> DeleteForChainAsync(Stores stores, Guid chainId)
    {
        var list = (await stores.Comments.ListAsync()).Where(c => c.ChainId == chainId).ToList();
        foreach (var c in list) await stores.Comments.DeleteAsync(c.Id);
        return list.Count;
    }

    private async Task<IReadOnlyList<Segment>> CurrentAsync(Guid itemId) =>
        await stores.Transcripts.GetAsync(itemId) is { } t ? t.Render().Select(r => r.Segment).ToList() : [];

    private static string Check(string? text, string what)
    {
        var t = NoteService.Tidy(text);
        if (t.Length == 0) throw new DomainException($"Write the {what} first.");
        if (t.Length > TextComment.MaxLength) throw new DomainException($"A {what} can be up to {TextComment.MaxLength} characters.");
        return t;
    }

    /// <summary>Why this person cannot comment, reply or resolve on this item now, or null when they can.</summary>
    private async Task<string?> WhyNotAsync(User actor, Item item)
    {
        if (item.Status == ItemStatus.Purged) return "This recording has been deleted.";
        if (item.IsLocked) return "The recording is approved, so its comments are locked.";
        if ((await stores.Items.ListAsync()).Any(i => i.SupersedesItemId == item.Id)) return "A newer version of this recording exists. Comment there.";
        if (await stores.Transcripts.GetAsync(item.Id) is null) return "There is no text to comment on yet.";
        if (item.AssignedUserId == actor.Id) return null; // also before accepting, to ask a question
        foreach (var c in new[] { Capability.Write, Capability.Reassign, Capability.Approve })
            if (PermissionEvaluator.Evaluate(actor, item, c).Allowed) return null;
        return "Only the people working on this recording can comment.";
    }

    private async Task<TextComment> RequireAsync(Item item, Guid id) =>
        await stores.Comments.GetAsync(id) is { } c && c.ChainId == item.ChainId ? c : throw new NotFoundException("That comment does not exist.");

    private static string Sha(string s) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(s)));

    private Task Audit(User actor, Item item, string type, params (string, object?)[] details) =>
        stores.Audit.AppendAsync(actor.Id, MeetingService.CapacityOf(actor, item), Product, item.Id, type, AuditChain.Details(details));
}
