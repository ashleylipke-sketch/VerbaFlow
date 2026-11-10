using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VerbaFlow.Core.Audit;
using VerbaFlow.Core.Domain;
using VerbaFlow.Core.Providers;

namespace VerbaFlow.Infrastructure.Services;

public sealed record NotificationView(Guid Id, string Text, string Group, Guid? ItemId, string Link, DateTimeOffset At, bool Read);
public sealed record NotificationList(int Unread, IReadOnlyList<NotificationView> Items);
public sealed record NotificationGroupView(string Key, string Label, bool Email);
public sealed record NotificationSettingsView(bool HasEmail, string? Email, bool EmailReal, IReadOnlyList<NotificationGroupView> Groups);

/// <summary>
/// Tells people when something needs them or concerns them. It reads the history (the audit trail) as it grows, so every
/// part of VerbaFlow that records an event is covered without changing it: assigned, returned, approved, reopened, deleted,
/// ready, failed, requests waiting for an administrator, and retention changes. It also looks for due dates. Each notice is
/// shown in VerbaFlow (the bell) and, unless the person switched that kind off, sent by email.
/// Notices are made once: their id comes from what caused them, so running twice changes nothing.
/// </summary>
public sealed class NotificationService(Stores stores, IEmailSender email, TimeProvider clock, FailureReporter reporter, string siteAddress)
{
    private static readonly Guid StateId = Guid.Parse("6e6f7469-6669-6361-7469-6f6e73746174");
    public static readonly TimeSpan DueEvery = TimeSpan.FromMinutes(10);
    private const int Batch = 500, KeepPerPerson = 200;
    private readonly SemaphoreSlim gate = new(1, 1);

    // ---------- reading the history ----------

    /// <summary>
    /// Turns new history entries into notices and looks for due dates. The first run starts at the end of the history, so
    /// nobody is told about things that happened before notifications existed.
    /// </summary>
    public async Task ProcessAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            var state = await stores.NotificationState.GetAsync(StateId);
            if (state is null)
            {
                state = new NotificationState(StateId, await stores.Audit.LastSeqAsync());
                await stores.NotificationState.UpsertAsync(StateId, state);
            }
            while (!ct.IsCancellationRequested)
            {
                var events = await stores.Audit.ListAfterAsync(state.LastSeq, Batch);
                if (events.Count == 0) break;
                var context = await Context.LoadAsync(stores);
                foreach (var e in events)
                {
                    // One entry that cannot be turned into a notice is reported and skipped, so it never holds up the rest.
                    try { foreach (var n in await NoticesFor(e, context)) await DeliverAsync(n, context); }
                    catch (Exception ex) when (ex is not OperationCanceledException) { await reporter.ReportAsync("notifications", e.ItemId, ex); }
                }
                state = state with { LastSeq = events[^1].Seq };
                await stores.NotificationState.UpsertAsync(StateId, state);
                if (events.Count < Batch) break;
            }
            var now = clock.GetUtcNow();
            if (state.DueCheckedAt is null || now - state.DueCheckedAt >= DueEvery)
            {
                var context = await Context.LoadAsync(stores);
                foreach (var n in DueNotices(context, now)) await DeliverAsync(n, context);
                await stores.NotificationState.UpsertAsync(StateId, state with { DueCheckedAt = now });
            }
        }
        finally { gate.Release(); }
    }

    private sealed record Notice(string Key, Guid UserId, string Group, string Text, Guid? ItemId, string Link);

    private sealed class Context
    {
        public required Dictionary<Guid, User> Users { get; init; }
        public required Dictionary<Guid, Item> Items { get; init; }
        public required List<User> Admins { get; init; }
        public static async Task<Context> LoadAsync(Stores s)
        {
            var users = (await s.Users.ListAsync()).ToDictionary(u => u.Id);
            return new Context
            {
                Users = users, Items = (await s.Items.ListAsync()).ToDictionary(i => i.Id),
                Admins = users.Values.Where(u => u.IsAdmin && !u.IsSupport).ToList(),
            };
        }
        public string Who(Guid? id) => id is { } g && Users.TryGetValue(g, out var u) ? u.Name : "Someone";
        public string Title(Guid? id) => id is { } g && Items.TryGetValue(g, out var i) && !string.IsNullOrWhiteSpace(i.Name) ? $"“{i.Name.Trim()}”" : "a recording";
    }

    private async Task<IEnumerable<Notice>> NoticesFor(AuditEvent e, Context c)
    {
        var d = Read(e.Details);
        var key = $"audit:{e.Seq}";
        var actor = e.ActorId;
        var item = e.ItemId is { } iid ? c.Items.GetValueOrDefault(iid) : null;
        string ItemLink(Guid? id) => id is { } g ? $"#/items/{g}" : "#/";
        IEnumerable<Notice> To(IEnumerable<Guid?> people, string group, string text, Guid? itemId, string link) =>
            people.OfType<Guid>().Distinct().Where(p => p != actor && c.Users.ContainsKey(p))
                .Select(p => new Notice(key, p, group, text, itemId, link));
        // The organisation the event is about: its recording's, else the one History names, else the actor's.
        var eventOrg = item?.Org
            ?? (GuidOf(d, "originalItemId") is { } oid && c.Items.TryGetValue(oid, out var original) ? original.Org : null)
            ?? GuidOf(d, "organisation")
            ?? (actor is { } aid && c.Users.TryGetValue(aid, out var au) ? au.Org : null)
            ?? Organisations.Default;
        var admins = c.Admins.Where(a => a.Org == eventOrg).ToList();
        IEnumerable<Notice> ToAdmins(string text, string link, IEnumerable<Guid>? except = null) =>
            To(admins.Select(a => (Guid?)a.Id).Where(a => except is null || !except.Contains(a!.Value)), NotificationGroups.Approvals, text, e.ItemId, link);

        switch (e.Type)
        {
            case "item.assigned" when GuidOf(d, "to") is { } to && item is not null && to != item.OwnerId:
                return To([to], NotificationGroups.Work, $"{c.Who(actor)} assigned {c.Title(item.Id)} to you.", item.Id, ItemLink(item.Id));
            case "note.added" when item is not null:
            {
                // The owner, the person it is assigned to, and everyone who already wrote a note on it.
                var writers = (await stores.Notes.ListAsync()).Where(n => n.ChainId == item.ChainId).Select(n => (Guid?)n.AuthorId);
                return To([item.OwnerId, item.AssignedUserId, .. writers], NotificationGroups.Notes,
                    $"{c.Who(actor)} left a note on {c.Title(item.Id)}.", item.Id, ItemLink(item.Id) + "?notes=1");
            }
            case "comment.added" when item is not null:
            {
                // The owner, the person it is assigned to, and everyone who already commented on this recording.
                var writers = (await stores.Comments.ListAsync()).Where(x => x.ChainId == item.ChainId).Select(x => (Guid?)x.AuthorId);
                return To([item.OwnerId, item.AssignedUserId, .. writers], NotificationGroups.Notes,
                    $"{c.Who(actor)} commented on {c.Title(item.Id)}.", item.Id, ItemLink(item.Id) + $"?comment={GuidOf(d, "comment")}");
            }
            case "comment.replied" when item is not null && GuidOf(d, "comment") is { } cid:
            {
                // Everyone in that comment's conversation, plus the owner and the person it is assigned to.
                var thread = await stores.Comments.GetAsync(cid);
                var people = thread is null ? [] : thread.Replies.Select(r => (Guid?)r.AuthorId).Append(thread.AuthorId);
                return To([item.OwnerId, item.AssignedUserId, .. people], NotificationGroups.Notes,
                    $"{c.Who(actor)} replied to a comment on {c.Title(item.Id)}.", item.Id, ItemLink(item.Id) + $"?comment={cid}");
            }
            case "item.returned_to_owner" when item is not null:
                return To([item.OwnerId], NotificationGroups.Work, $"{c.Who(actor)} returned {c.Title(item.Id)} to you.", item.Id, ItemLink(item.Id));
            case "item.approved" when item is not null:
                return To([item.OwnerId, item.AssignedUserId], NotificationGroups.Work, $"{c.Title(item.Id)} was approved by {c.Who(actor)}.", item.Id, ItemLink(item.Id));
            case "item.reopened" when item is not null:
                return To([GuidOf(d, "requestedBy")], NotificationGroups.Work,
                    $"{c.Title(item.Id)} was reopened as you asked. The new version is open for changes.", item.Id, ItemLink(item.Id));
            case "item.deleted" when item is not null:
                return To([item.OwnerId], NotificationGroups.Work,
                    $"{c.Title(item.Id)} was deleted by {(actor is null || actor == Guid.Empty ? "the retention rules" : c.Who(actor))}.", item.Id, "#/");
            case "reopen.rejected":
            {
                var req = await stores.Reopen.GetAsync(GuidOf(d, "requestId") ?? default);
                return req is null ? [] : To([req.RequestedBy], NotificationGroups.Work,
                    $"{c.Who(actor)} turned down reopening {c.Title(req.ItemId)}.", req.ItemId, ItemLink(req.ItemId));
            }
            case "delete.rejected":
            {
                var req = await stores.Deletes.GetAsync(GuidOf(d, "requestId") ?? default);
                return req is null ? [] : To([req.RequestedBy], NotificationGroups.Work,
                    $"{c.Who(actor)} turned down deleting {c.Title(req.ItemId)}.", req.ItemId, ItemLink(req.ItemId));
            }
            case "reopen.requested":
                return ToAdmins($"{c.Who(actor)} asks to reopen {c.Title(GuidOf(d, "originalItemId"))}. Two administrators must agree.", "#/reopen");
            case "delete.requested":
                return ToAdmins($"{c.Who(actor)} asks to delete {c.Title(e.ItemId)}. Two administrators must agree.", "#/reopen");
            case "reopen.approved":
            {
                var req = await stores.Reopen.GetAsync(GuidOf(d, "requestId") ?? default);
                return req is null || !req.IsOpen ? [] : ToAdmins(
                    $"{c.Who(actor)} agreed to reopen {c.Title(req.ItemId)}. One more administrator must agree.", "#/reopen", [.. req.Approvals, req.RequestedBy]);
            }
            case "delete.approved":
            {
                var req = await stores.Deletes.GetAsync(GuidOf(d, "requestId") ?? default);
                return req is null || !req.IsOpen || req.IsApproved ? [] : ToAdmins(
                    $"{c.Who(actor)} agreed to delete {c.Title(req.ItemId)}. One more administrator must agree.", "#/reopen", [.. req.Approvals, req.RequestedBy]);
            }
            case "retention.proposed":
                return ToAdmins($"{c.Who(actor)} proposed new retention rules. Another administrator must agree before they apply.", "#/retention");
            case "retention.agreed":
                return ToAdmins($"{c.Who(actor)} agreed the new retention rules.", "#/retention");
            case "processing.completed" when item is not null && item.Mode != ItemMode.Speech:
                return To([item.OwnerId], NotificationGroups.Ready, $"{c.Title(item.Id)} is ready to check.", item.Id, ItemLink(item.Id));
            case "processing.failed" when item is not null && Text(d, "kind") == "plan-limit":
                return To([item.OwnerId], NotificationGroups.Ready,
                    $"{c.Title(item.Id)} is waiting: this month's minutes are used up. It can be converted once there are minutes again (Retry on the recording).", item.Id, ItemLink(item.Id));
            case "plan.usage_warning" when d.TryGetValue("percent", out var pv) && pv.ToString() is var pct:
            {
                var org = Text(d, "account") == "organisation";
                var who = org ? admins.Select(a => (Guid?)a.Id) : [Guid.TryParse(Text(d, "account"), out var g) ? g : null];
                var invoiced = org && Text(d, "plan") is { } pl && pl != Plans.TrialId;
                var text = (org ? "Your organisation has" : "You have") + (pct == "100"
                    ? invoiced ? " used this month's included minutes. Extra minutes will be on the next invoice." : " used all of this month's minutes. New conversions will wait until next month or a bigger plan."
                    : $" used {pct}% of this month's minutes.");
                return To(who, NotificationGroups.Plan, text, null, "#/plan");
            }
            case "processing.failed" when item is not null:
                return To([item.OwnerId], NotificationGroups.Ready,
                    $"{c.Title(item.Id)} could not be converted to text. Open it to try again.", item.Id, ItemLink(item.Id));
            case "meet.state_changed" when Text(d, "state") == nameof(NoteTakerState.Failed):
            {
                var nt = await stores.NoteTakers.GetAsync(GuidOf(d, "noteTaker") ?? default);
                var name = string.IsNullOrWhiteSpace(nt?.Name) ? "your online meeting" : $"“{nt!.Name.Trim()}”";
                var why = Text(d, "reason") is { Length: > 0 } r ? $" {r.TrimEnd('.')}." : "";
                return nt is null ? [] : To(nt.Requesters.Select(x => (Guid?)x), NotificationGroups.Ready,
                    $"The note-taker could not record {name}.{why}", null, "#/meet");
            }
            default:
                return [];
        }
    }

    /// <summary>Due tomorrow, due today and overdue, each told once, to whoever has the recording now.</summary>
    private IEnumerable<Notice> DueNotices(Context c, DateTimeOffset now)
    {
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, TimeZoneInfo.Local).Date);
        var superseded = c.Items.Values.Where(i => i.SupersedesItemId is not null).Select(i => i.SupersedesItemId!.Value).ToHashSet();
        foreach (var i in c.Items.Values)
        {
            if (i.DueDate is not { } due || superseded.Contains(i.Id)) continue;
            if (i.Status is ItemStatus.Completed or ItemStatus.Purged or ItemStatus.Draft) continue;
            var who = i.Status is ItemStatus.AwaitingAssignee or ItemStatus.WithAssignee && i.AssignedUserId is { } a ? a : i.OwnerId;
            var (kind, text) = due == today.AddDays(1) ? ("tomorrow", $"{c.Title(i.Id)} is due tomorrow.")
                : due == today ? ("today", $"{c.Title(i.Id)} is due today.")
                : due < today ? ("overdue", $"{c.Title(i.Id)} is overdue. It was due on {due.ToString("d MMMM", CultureInfo.InvariantCulture)}.")
                : default;
            if (kind is null || !c.Users.ContainsKey(who)) continue;
            yield return new Notice($"due:{i.Id}:{due:yyyy-MM-dd}:{kind}:{who}", who, NotificationGroups.Due, text, i.Id, $"#/items/{i.Id}");
        }
    }

    private async Task DeliverAsync(Notice n, Context c)
    {
        var id = IdOf(n.Key, n.UserId);
        if (await stores.Notifications.GetAsync(id) is not null) return; // already told
        var note = new Notification(id, n.UserId, n.Group, n.Text, n.ItemId, n.Link, clock.GetUtcNow());
        var user = c.Users[n.UserId];
        var settings = await stores.NotificationSettings.GetAsync(n.UserId);
        string outcome;
        if (settings?.EmailOff.Contains(n.Group) == true) outcome = "not wanted";
        else if (!LooksLikeEmail(user.Email)) outcome = "no address";
        else
        {
            try
            {
                var body = $"{n.Text}\n\nOpen it in VerbaFlow: {siteAddress.TrimEnd('/')}/{n.Link}\n\n"
                    + "You can choose which notices come by email in VerbaFlow under Notifications (the bell at the top).";
                await email.SendAsync(new EmailMessage([user.Email], "VerbaFlow: " + Shorten(n.Text, 120), body, []), CancellationToken.None);
                outcome = "sent";
            }
            catch (Exception ex)
            {
                var rep = await reporter.ReportAsync("notifications", n.ItemId, ex);
                outcome = $"failed ({rep.Reference})";
            }
        }
        await stores.Notifications.UpsertAsync(id, note with { Email = outcome });
    }

    // ---------- what a person sees ----------

    public async Task<NotificationList> ListAsync(User user, int limit = 50)
    {
        // Newest first; notices made at the same moment keep the order they were made in (the store lists them oldest first).
        var mine = (await stores.Notifications.ListAsync()).Where(n => n.UserId == user.Id).Reverse().OrderByDescending(n => n.At).ToList();
        // Old notices are tidied away so the list stays short.
        foreach (var old in mine.Skip(KeepPerPerson)) await stores.Notifications.DeleteAsync(old.Id);
        return new NotificationList(mine.Take(KeepPerPerson).Count(n => n.ReadAt is null),
            mine.Take(limit).Select(n => new NotificationView(n.Id, n.Text, n.Group, n.ItemId, n.Link, n.At, n.ReadAt is not null)).ToList());
    }

    /// <summary>Marks one notice (or, with no id, all of them) as read.</summary>
    public async Task<NotificationList> MarkReadAsync(User user, Guid? id)
    {
        var now = clock.GetUtcNow();
        foreach (var n in (await stores.Notifications.ListAsync()).Where(n => n.UserId == user.Id && n.ReadAt is null && (id is null || n.Id == id)))
            await stores.Notifications.UpsertAsync(n.Id, n with { ReadAt = now });
        return await ListAsync(user);
    }

    public async Task<NotificationSettingsView> SettingsAsync(User user)
    {
        var s = await stores.NotificationSettings.GetAsync(user.Id);
        var real = !email.Name.StartsWith("stand-in", StringComparison.OrdinalIgnoreCase);
        return new NotificationSettingsView(LooksLikeEmail(user.Email), LooksLikeEmail(user.Email) ? user.Email : null, real,
            NotificationGroups.All.Where(g => !g.AdminsOnly || user.IsAdmin)
                .Select(g => new NotificationGroupView(g.Key, g.Label, s?.EmailOff.Contains(g.Key) != true)).ToList());
    }

    /// <summary>Sets which kinds of notice come by email (the rest are only shown in VerbaFlow).</summary>
    public async Task<NotificationSettingsView> SetEmailAsync(User user, IReadOnlyList<string> emailOn)
    {
        var known = NotificationGroups.All.Select(g => g.Key).ToHashSet();
        var off = known.Where(k => !emailOn.Contains(k)).Order().ToList();
        await stores.NotificationSettings.UpsertAsync(user.Id, new NotificationSettings(user.Id, off));
        await stores.Audit.AppendAsync(user.Id, "user", "platform", null, "notifications.settings_changed",
            AuditChain.Details(("emailOff", string.Join(",", off))));
        return await SettingsAsync(user);
    }

    // ---------- helpers ----------

    private static Guid IdOf(string key, Guid user) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes($"{key}|{user}")).AsSpan(0, 16));

    private static bool LooksLikeEmail(string? s) => !string.IsNullOrWhiteSpace(s) && s.Contains('@') && !s.Contains(' ');

    private static string Shorten(string s, int max) => s.Length <= max ? s : s[..(max - 1)].TrimEnd() + "…";

    private static Dictionary<string, JsonElement> Read(string details)
    {
        try { return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(details) ?? []; }
        catch (JsonException) { return []; }
    }

    private static string? Text(Dictionary<string, JsonElement> d, string key) =>
        d.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static Guid? GuidOf(Dictionary<string, JsonElement> d, string key) =>
        Guid.TryParse(Text(d, key), out var g) ? g : null;
}
