using VerbaFlow.Core.Audit;
using VerbaFlow.Core.Domain;
using VerbaFlow.Core.Security;

namespace VerbaFlow.Infrastructure.Services;

public sealed record RetentionChange(RetentionRules? Meetings, RetentionRules? Dictations, string? Note);
public sealed record RetentionHoldBody(string? Reason);

public sealed record RetentionProposalView(RetentionRules Meetings, RetentionRules Dictations, string ProposedBy, DateTimeOffset At, string? Note, bool YouProposed);

/// <param name="Action">"delete" (the whole recording) or "remove-recording" (the sound or video only; the text is kept).</param>
public sealed record RetentionDueRow(Guid ItemId, string Name, string Mode, string Status, string Action, DateTimeOffset DueAt);

public sealed record RetentionHoldRow(Guid ItemId, string Name, string By, DateTimeOffset At, string Reason);

/// <param name="Due">Administrators only: what the rules will delete in the next 30 days, soonest first.</param>
public sealed record RetentionOverview(RetentionRules Meetings, RetentionRules Dictations, DateTimeOffset? AgreedAt, IReadOnlyList<string> AgreedBy,
    DateTimeOffset? AppliesFrom, RetentionProposalView? Proposal, bool IsAdmin, IReadOnlyList<RetentionDueRow> Due, IReadOnlyList<RetentionHoldRow> Holds);

/// <param name="Note">A plain sentence about what the retention rules will do to this recording, or null when they do nothing.</param>
public sealed record ItemRetention(string? Note, bool Held, string? HoldReason, bool CanHold);

/// <summary>
/// Retention rules: how long recordings are kept. Administrators set separate rules for meetings and for dictations
/// (Dictate and Speech): delete approved items N days after approval, remove only the sound or video N days after approval
/// (the text stays), and delete never-approved items after N days without activity. A change needs a second administrator
/// to agree, and nothing is deleted under new rules until a week later. Administrators can put a recording on hold so the
/// rules never delete it. Deleting keeps the tombstone and the whole History, like any delete.
/// </summary>
public sealed class RetentionService(Stores stores, MeetingService meetings, TimeProvider clock, FailureReporter reporter)
{
    private const string Product = "platform";
    public const string Delete = "delete", RemoveRecording = "remove-recording";
    public static readonly TimeSpan ShowAhead = TimeSpan.FromDays(30);

    private sealed record Planned(Item Newest, List<Item> Chain, string Action, DateTimeOffset DueAt, int AfterDays, string Why);

    public async Task<RetentionPolicy> PolicyAsync() => await stores.Retention.GetAsync(RetentionPolicy.Id) ?? RetentionPolicy.KeepEverything;

    public async Task<RetentionOverview> OverviewAsync(User actor)
    {
        var p = await PolicyAsync();
        var users = (await stores.Users.ListAsync()).ToDictionary(u => u.Id, u => u.Name);
        string Name(Guid id) => users.GetValueOrDefault(id, "?");
        IReadOnlyList<RetentionDueRow> due = [];
        IReadOnlyList<RetentionHoldRow> holds = [];
        if (actor.IsAdmin)
        {
            var now = clock.GetUtcNow();
            due = (await PlanAsync(p)).Where(x => x.DueAt <= now + ShowAhead).OrderBy(x => x.DueAt)
                .Select(x => new RetentionDueRow(x.Newest.Id, x.Newest.Name, x.Newest.Mode.ToString(), StatusLabels.For(x.Newest.Mode, x.Newest.Status), x.Action, x.DueAt)).ToList();
            var items = await stores.Items.ListAsync();
            holds = (await stores.RetentionHolds.ListAsync()).Select(h =>
            {
                var newest = items.Where(i => i.ChainId == h.ChainId).OrderByDescending(i => i.VersionNo).FirstOrDefault();
                return new RetentionHoldRow(newest?.Id ?? h.ChainId, newest?.Name ?? "?", Name(h.By), h.At, h.Reason);
            }).OrderBy(h => h.Name).ToList();
        }
        var proposal = p.Proposal is { } pr ? new RetentionProposalView(pr.Meetings, pr.Dictations, Name(pr.ProposedBy), pr.At, pr.Note, pr.ProposedBy == actor.Id) : null;
        return new(p.Meetings, p.Dictations, p.AgreedAt, (p.AgreedBy ?? []).Select(Name).ToList(), p.AppliesFrom, proposal, actor.IsAdmin, due, holds);
    }

    /// <summary>An administrator proposes new rules. A different administrator must agree before they count.</summary>
    public async Task<RetentionOverview> ProposeAsync(User admin, RetentionChange change)
    {
        RequireAdmin(admin);
        var meetingsRules = change.Meetings ?? new RetentionRules();
        var dictationRules = change.Dictations ?? new RetentionRules();
        meetingsRules.Check("Meetings");
        dictationRules.Check("Dictations");
        var p = await PolicyAsync();
        if (p.Proposal is not null) throw new DomainException("A change is already waiting for a second administrator. Agree to it, reject it or withdraw it first.");
        if (p.Meetings == meetingsRules && p.Dictations == dictationRules) throw new DomainException("These are the rules already in use.");
        var note = string.IsNullOrWhiteSpace(change.Note) ? null : change.Note.Trim();
        if (note?.Length > 500) throw new DomainException("The note is too long (500 characters at most).");
        await stores.Retention.UpsertAsync(RetentionPolicy.Id, p with { Proposal = new RetentionProposal(meetingsRules, dictationRules, admin.Id, clock.GetUtcNow(), note) });
        await Audit(admin, null, "retention.proposed", Details(meetingsRules, dictationRules, ("note", note)));
        return await OverviewAsync(admin);
    }

    /// <summary>A second administrator agrees: the proposed rules replace the current ones, and start deleting a week later.</summary>
    public async Task<RetentionOverview> AgreeAsync(User admin)
    {
        RequireAdmin(admin);
        var p = await PolicyAsync();
        var pr = p.Proposal ?? throw new DomainException("There is no change waiting.");
        if (pr.ProposedBy == admin.Id) throw new DomainException("A different administrator must agree to the change you proposed.");
        var now = clock.GetUtcNow();
        var agreed = new RetentionPolicy(pr.Meetings, pr.Dictations, now, [pr.ProposedBy, admin.Id], now + RetentionPolicy.Grace, null);
        await stores.Retention.UpsertAsync(RetentionPolicy.Id, agreed);
        await Audit(admin, null, "retention.agreed", Details(pr.Meetings, pr.Dictations, ("appliesFrom", agreed.AppliesFrom)));
        return await OverviewAsync(admin);
    }

    /// <summary>The proposer withdraws the change, or another administrator rejects it. The current rules stay.</summary>
    public async Task<RetentionOverview> DropProposalAsync(User admin)
    {
        RequireAdmin(admin);
        var p = await PolicyAsync();
        var pr = p.Proposal ?? throw new DomainException("There is no change waiting.");
        await stores.Retention.UpsertAsync(RetentionPolicy.Id, p with { Proposal = null });
        await Audit(admin, null, pr.ProposedBy == admin.Id ? "retention.withdrawn" : "retention.rejected");
        return await OverviewAsync(admin);
    }

    /// <summary>An administrator puts a recording (all its versions) on hold: the retention rules never delete it.</summary>
    public async Task<ItemRetention> HoldAsync(User admin, Guid itemId, string? reason)
    {
        RequireAdmin(admin);
        var item = await stores.RequireItemAsync(itemId);
        if (item.Status == ItemStatus.Purged) throw new DomainException("This recording has already been deleted.");
        if (string.IsNullOrWhiteSpace(reason)) throw new DomainException("Say why this recording must be kept (for example a legal case or a complaint).");
        if (reason.Trim().Length > 500) throw new DomainException("The reason is too long (500 characters at most).");
        if (await stores.RetentionHolds.GetAsync(item.ChainId) is not null) throw new DomainException("This recording is already on hold.");
        await stores.RetentionHolds.UpsertAsync(item.ChainId, new RetentionHold(item.ChainId, admin.Id, clock.GetUtcNow(), reason.Trim()));
        await Audit(admin, item.Id, "retention.held", ("reason", reason.Trim()));
        return await ForItemAsync(admin, itemId);
    }

    public async Task<ItemRetention> ReleaseAsync(User admin, Guid itemId)
    {
        RequireAdmin(admin);
        var item = await stores.RequireItemAsync(itemId);
        if (!await stores.RetentionHolds.DeleteAsync(item.ChainId)) throw new DomainException("This recording is not on hold.");
        await Audit(admin, item.Id, "retention.released");
        return await ForItemAsync(admin, itemId);
    }

    /// <summary>What the rules will do to one recording, for its page.</summary>
    public async Task<ItemRetention> ForItemAsync(User actor, Guid itemId)
    {
        var item = await stores.RequireItemAsync(itemId);
        PermissionEvaluator.Require(actor, item, Capability.Read);
        if (item.Status == ItemStatus.Purged) return new(null, false, null, false);
        var hold = await stores.RetentionHolds.GetAsync(item.ChainId);
        var removed = await meetings.RecordingRemovedAsync(item);
        if (hold is not null)
        {
            var by = (await stores.Users.GetAsync(hold.By))?.Name ?? "an administrator";
            return new($"On hold: the retention rules will not delete this recording (put on hold by {by} on {hold.At:d MMM yyyy}).", true, hold.Reason, actor.IsAdmin);
        }
        var planned = (await PlanAsync(await PolicyAsync())).Where(x => x.Chain.Any(c => c.Id == itemId)).OrderBy(x => x.DueAt).ToList();
        var parts = new List<string>();
        if (removed is not null) parts.Add(removed);
        foreach (var x in planned)
            parts.Add(x.Action == Delete
                ? $"Under the retention rules this recording will be deleted on {x.DueAt:d MMM yyyy}."
                : $"Under the retention rules its sound will be removed on {x.DueAt:d MMM yyyy}; the text is kept.");
        return new(parts.Count == 0 ? null : string.Join(" ", parts), false, null, actor.IsAdmin);
    }

    /// <summary>Deletes what the rules say is due. Run every hour by the worker. Returns how many recordings were changed.</summary>
    public async Task<int> ApplyAsync(CancellationToken ct = default)
    {
        var p = await PolicyAsync();
        var now = clock.GetUtcNow();
        var done = 0;
        foreach (var x in (await PlanAsync(p)).Where(x => x.DueAt <= now).OrderBy(x => x.DueAt))
        {
            ct.ThrowIfCancellationRequested();
            if (x.Chain.Any(i => i.Processing == ProcessingState.Running)) continue; // next time
            try
            {
                if (x.Action == Delete)
                {
                    if (await meetings.DeleteByRetentionAsync(x.Newest.ChainId, x.Why, p.AgreedBy ?? [])) done++;
                }
                else
                {
                    int removed = 0, failed = 0;
                    foreach (var source in x.Chain.Select(i => i.MediaSourceItemId).Distinct())
                    {
                        var (r, f) = await stores.Media.DeleteContentAsync(source);
                        removed += r; failed += f;
                        await stores.RecordingRemovals.UpsertAsync(source, new RecordingRemoval(source, now, x.AfterDays));
                    }
                    await stores.Audit.AppendAsync(null, "system", Product, x.Newest.Id, "retention.recording_removed", AuditChain.Details(
                        ("reason", x.Why), ("filesRemoved", removed), ("filesNotRemoved", failed == 0 ? null : failed),
                        ("agreedBy", string.Join(", ", await NamesAsync(p.AgreedBy)))));
                    done++;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { await reporter.ReportAsync("retention", x.Newest.Id, ex); }
        }
        return done;
    }

    /// <summary>What the rules will do to each recording, and when (never before the rules apply).</summary>
    private async Task<List<Planned>> PlanAsync(RetentionPolicy p)
    {
        var list = new List<Planned>();
        if (p.Meetings.KeepsEverything && p.Dictations.KeepsEverything) return list;
        var items = await stores.Items.ListAsync();
        var holds = (await stores.RetentionHolds.ListAsync()).Select(h => h.ChainId).ToHashSet();
        var removals = (await stores.RecordingRemovals.ListAsync()).Select(r => r.SourceItemId).ToHashSet();
        Dictionary<Guid, DateTimeOffset>? lastActivity = null;
        var from = p.AppliesFrom ?? DateTimeOffset.MinValue;
        DateTimeOffset Due(DateTimeOffset at) => at < from ? from : at;

        foreach (var chain in items.GroupBy(i => i.ChainId).Select(g => g.OrderBy(i => i.VersionNo).ToList()))
        {
            var newest = chain[^1];
            if (newest.Status == ItemStatus.Purged || holds.Contains(newest.ChainId)) continue;
            var rules = p.For(newest);
            var what = newest.IsDictation ? "dictations" : "meetings";
            if (newest.Status == ItemStatus.Completed && newest.ApprovedAt is { } approved)
            {
                if (rules.DeleteApprovedAfterDays is { } d)
                    list.Add(new(newest, chain, Delete, Due(approved.AddDays(d)), d, $"Retention rules: approved {what} are kept for {d} days after approval."));
                if (rules.RemoveRecordingAfterDays is { } r && !chain.Select(i => i.MediaSourceItemId).Distinct().All(removals.Contains))
                    list.Add(new(newest, chain, RemoveRecording, Due(approved.AddDays(r)), r, $"Retention rules: the recordings of approved {what} are kept for {r} days after approval."));
            }
            else if (chain.All(i => i.ApprovedAt is null) && rules.DeleteUnapprovedAfterDays is { } u)
            {
                if (lastActivity is null)
                {
                    lastActivity = [];
                    foreach (var e in await stores.Audit.ListAsync())
                        if (e.ItemId is { } id && (!lastActivity.TryGetValue(id, out var t) || e.At > t)) lastActivity[id] = e.At;
                }
                var last = chain.Select(i => lastActivity.TryGetValue(i.Id, out var t) ? t : i.CreatedAt).Append(newest.CreatedAt).Max();
                list.Add(new(newest, chain, Delete, Due(last.AddDays(u)), u, $"Retention rules: {what} that are never approved are kept for {u} days after anything last happened to them."));
            }
        }
        return list;
    }

    private async Task<IEnumerable<string>> NamesAsync(IReadOnlyList<Guid>? ids)
    {
        var users = (await stores.Users.ListAsync()).ToDictionary(u => u.Id, u => u.Name);
        return (ids ?? []).Select(i => users.GetValueOrDefault(i, "?"));
    }

    private static (string, object?)[] Details(RetentionRules m, RetentionRules d, params (string, object?)[] more) =>
    [
        ("meetingsDeleteApprovedAfterDays", m.DeleteApprovedAfterDays), ("meetingsRemoveRecordingAfterDays", m.RemoveRecordingAfterDays),
        ("meetingsDeleteUnapprovedAfterDays", m.DeleteUnapprovedAfterDays), ("dictationsDeleteApprovedAfterDays", d.DeleteApprovedAfterDays),
        ("dictationsRemoveRecordingAfterDays", d.RemoveRecordingAfterDays), ("dictationsDeleteUnapprovedAfterDays", d.DeleteUnapprovedAfterDays),
        .. more,
    ];

    private static void RequireAdmin(User u)
    {
        if (!u.IsAdmin) throw new ForbiddenException("Only administrators can change the retention rules or put recordings on hold.");
    }

    private Task Audit(User actor, Guid? itemId, string type, params (string, object?)[] details) =>
        stores.Audit.AppendAsync(actor.Id, "admin", Product, itemId, type, AuditChain.Details(details));
}
