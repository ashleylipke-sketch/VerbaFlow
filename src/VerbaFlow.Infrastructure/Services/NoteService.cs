using System.Security.Cryptography;
using System.Text;
using VerbaFlow.Core.Audit;
using VerbaFlow.Core.Domain;
using VerbaFlow.Core.Security;

namespace VerbaFlow.Infrastructure.Services;

public sealed record NoteRow(Guid Id, string Text, string By, Guid ById, DateTimeOffset At, bool Done, string? DoneBy, DateTimeOffset? DoneAt, bool CanRemove);

/// <param name="CanAdd">Whether this person can write notes and tick them off now.</param>
/// <param name="Why">A plain sentence when they cannot, or null.</param>
public sealed record NoteList(IReadOnlyList<NoteRow> Notes, bool CanAdd, string? Why);

/// <summary>
/// Notes left on a recording for the people working on it: the owner, the person it is assigned to, and administrators
/// or department admins who look after it. Anyone who can open the recording can read them. A note can be ticked as dealt
/// with (and unticked); only its writer can take it away. Approval locks them. The History records that a note was
/// added, ticked or removed, with a fingerprint of its words but not the words themselves. The other people working on the
/// recording are told through the bell (see <see cref="NotificationService"/>).
/// </summary>
public sealed class NoteService(Stores stores, TimeProvider clock)
{
    private const string Product = "speak";

    public async Task<NoteList> ListAsync(User actor, Guid itemId)
    {
        var item = await stores.RequireItemAsync(itemId);
        PermissionEvaluator.Require(actor, item, Capability.Read);
        var why = await WhyNotAsync(actor, item);
        var users = (await stores.Users.ListAsync()).ToDictionary(u => u.Id, u => u.Name);
        var notes = item.Status == ItemStatus.Purged ? [] : (await ForChainAsync(item.ChainId))
            .Select(n => new NoteRow(n.Id, n.Text, users.GetValueOrDefault(n.AuthorId, "?"), n.AuthorId, n.At, n.Done,
                n.DoneBy is { } d ? users.GetValueOrDefault(d, "?") : null, n.DoneAt, why is null && n.AuthorId == actor.Id))
            .ToList();
        return new(notes, why is null, why);
    }

    public async Task<IReadOnlyList<ItemNote>> ForChainAsync(Guid chainId) =>
        (await stores.Notes.ListAsync()).Where(n => n.ChainId == chainId).OrderBy(n => n.At).ToList();

    public async Task<NoteList> AddAsync(User actor, Guid itemId, string? text)
    {
        var item = await stores.RequireItemAsync(itemId);
        PermissionEvaluator.Require(actor, item, Capability.Read);
        if (await WhyNotAsync(actor, item) is { } why) throw new DomainException(why);
        var t = Tidy(text);
        if (t.Length == 0) throw new DomainException("Write the note first.");
        if (t.Length > ItemNote.MaxLength) throw new DomainException($"A note can be up to {ItemNote.MaxLength} characters.");
        if ((await ForChainAsync(item.ChainId)).Count >= ItemNote.MaxPerItem)
            throw new DomainException($"A recording can have up to {ItemNote.MaxPerItem} notes. Remove some that are dealt with first.");
        var note = new ItemNote(Guid.NewGuid(), item.ChainId, item.Id, actor.Id, t, clock.GetUtcNow());
        await stores.Notes.UpsertAsync(note.Id, note);
        await Audit(actor, item, "note.added", ("note", note.Id), ("characters", t.Length), ("sha256", Sha(t)));
        return await ListAsync(actor, itemId);
    }

    public async Task<NoteList> SetDoneAsync(User actor, Guid itemId, Guid noteId, bool done)
    {
        var item = await stores.RequireItemAsync(itemId);
        PermissionEvaluator.Require(actor, item, Capability.Read);
        var note = await RequireAsync(item, noteId);
        if (await WhyNotAsync(actor, item) is { } why) throw new DomainException(why);
        if (note.Done == done) return await ListAsync(actor, itemId);
        note = done ? note with { DoneBy = actor.Id, DoneAt = clock.GetUtcNow() } : note with { DoneBy = null, DoneAt = null };
        await stores.Notes.UpsertAsync(note.Id, note);
        await Audit(actor, item, done ? "note.done" : "note.reopened", ("note", note.Id));
        return await ListAsync(actor, itemId);
    }

    public async Task<NoteList> RemoveAsync(User actor, Guid itemId, Guid noteId)
    {
        var item = await stores.RequireItemAsync(itemId);
        PermissionEvaluator.Require(actor, item, Capability.Read);
        var note = await RequireAsync(item, noteId);
        if (await WhyNotAsync(actor, item) is { } why) throw new DomainException(why);
        if (note.AuthorId != actor.Id) throw new ForbiddenException("Only the person who wrote a note can remove it. You can tick it as dealt with instead.");
        await stores.Notes.DeleteAsync(note.Id);
        await Audit(actor, item, "note.removed", ("note", note.Id), ("sha256", Sha(note.Text)));
        return await ListAsync(actor, itemId);
    }

    /// <summary>Open notes (not ticked as dealt with) per recording, by chain, for the lists.</summary>
    public static async Task<Dictionary<Guid, int>> OpenCountsAsync(Stores stores) =>
        (await stores.Notes.ListAsync()).Where(n => !n.Done).GroupBy(n => n.ChainId).ToDictionary(g => g.Key, g => g.Count());

    /// <summary>Deletes every note of a recording when the recording itself is deleted. Returns how many there were.</summary>
    public static async Task<int> DeleteForChainAsync(Stores stores, Guid chainId)
    {
        var list = (await stores.Notes.ListAsync()).Where(n => n.ChainId == chainId).ToList();
        foreach (var n in list) await stores.Notes.DeleteAsync(n.Id);
        return list.Count;
    }

    /// <summary>Why this person cannot write or tick notes on this item now, or null when they can.</summary>
    private async Task<string?> WhyNotAsync(User actor, Item item)
    {
        if (item.Status == ItemStatus.Purged) return "This recording has been deleted.";
        if (item.IsLocked) return "The recording is approved, so its notes are locked.";
        if ((await stores.Items.ListAsync()).Any(i => i.SupersedesItemId == item.Id)) return "A newer version of this recording exists. Leave notes there.";
        if (item.AssignedUserId == actor.Id) return null; // also before accepting, to ask a question
        foreach (var c in new[] { Capability.Write, Capability.Reassign, Capability.Approve })
            if (PermissionEvaluator.Evaluate(actor, item, c).Allowed) return null;
        return "Only the people working on this recording can leave notes.";
    }

    private async Task<ItemNote> RequireAsync(Item item, Guid noteId) =>
        await stores.Notes.GetAsync(noteId) is { } n && n.ChainId == item.ChainId ? n : throw new NotFoundException("That note does not exist.");

    /// <summary>Line endings made plain, spaces at the ends of lines and of the note removed, at most one empty line in a row.</summary>
    public static string Tidy(string? text)
    {
        var lines = (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').Select(l => l.TrimEnd()).ToList();
        var sb = new StringBuilder();
        var blank = 0;
        foreach (var l in lines)
        {
            if (l.Length == 0) { if (++blank > 1) continue; } else blank = 0;
            sb.Append(l).Append('\n');
        }
        return new string(sb.ToString().Where(c => c == '\n' || !char.IsControl(c)).ToArray()).Trim();
    }

    private static string Sha(string s) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(s)));

    private Task Audit(User actor, Item item, string type, params (string, object?)[] details) =>
        stores.Audit.AppendAsync(actor.Id, MeetingService.CapacityOf(actor, item), Product, item.Id, type, AuditChain.Details(details));
}
