using System.Text.Json;
using VerbaFlow.Core.Audit;
using VerbaFlow.Core.Domain;
using VerbaFlow.Infrastructure.Persistence;

namespace VerbaFlow.Infrastructure.Services;

/// <summary>One person's work: what they have now, what they did in the period, and how long their steps took (medians, in hours).</summary>
public sealed record WorkloadPerson(Guid UserId, string Name,
    int AsAuthor, int ToAccept, int BeingTyped, int Overdue, int DueToday,
    int AssignedToThem, int Returned, int Approved,
    double? HoursToAccept, double? HoursTyping, double? HoursToApprove);

/// <summary>A recording waiting on someone: overdue, or waiting the longest.</summary>
public sealed record WorkloadItem(Guid ItemId, string Name, string Mode, string Holder, string Stage, DateOnly? Due, int? DaysLate, DateTimeOffset Since);

public sealed record WorkloadView(int Days, string Mode,
    int Open, int Overdue, int DueToday, int ApprovedInPeriod, double? DaysToApprove,
    IReadOnlyList<WorkloadPerson> People, IReadOnlyList<WorkloadItem> OverdueItems, IReadOnlyList<WorkloadItem> WaitingLongest,
    Guid? Department, IReadOnlyList<PersonOption> Departments, bool CanSeeAll);

/// <summary>
/// The administrators' overview of who has what. What people have now comes from the recordings themselves; what was done in the
/// period and how long each step took comes from the History (assigned, accepted, returned, approved), so nothing new is recorded.
/// Administrators see everyone, or one department; a department admin sees only the recordings of their departments' members.
/// </summary>
public sealed class WorkloadService(Stores stores, TimeProvider clock)
{
    public static readonly int[] Periods = [7, 30, 90, 365];

    /// <summary>History entries that move a recording to someone; opening, playing or exporting it does not.</summary>
    private static readonly HashSet<string> Steps = ["item.created", "item.imported", "processing.completed", "processing.failed",
        "item.assigned", "item.opened_by_assignee", "item.returned_to_owner", "item.reopened_version_created", "draft.finished", "recording.changed"];

    public async Task<WorkloadView> OverviewAsync(User actor, string? mode, int? days, Guid? department = null)
    {
        var departments = await stores.Departments.ListAsync();
        var mine = actor.IsAdmin ? departments : actor.IsSupport ? [] : departments.Where(d => d.Leads.Contains(actor.Id)).ToList();
        if (!actor.IsAdmin && mine.Count == 0)
            throw new ForbiddenException("Only administrators and department admins can see the workload overview.");
        var chosen = department is { } dep ? mine.FirstOrDefault(d => d.Id == dep) ?? throw new NotFoundException("That department does not exist, or you are not its department admin.") : null;
        var shown = chosen is not null ? [chosen] : actor.IsAdmin ? null : mine;
        var period = days is { } d && Periods.Contains(d) ? d : 30;
        ItemMode? only = mode?.ToLowerInvariant() switch
        {
            "meeting" => ItemMode.Meeting, "dictate" => ItemMode.Dictate, "speech" => ItemMode.Speech, _ => null,
        };
        var now = clock.GetUtcNow();
        var from = now.AddDays(-period);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, TimeZoneInfo.Local).Date);

        var users = (await stores.Users.ListAsync()).Where(u => !u.IsSupport).ToDictionary(u => u.Id);
        // Whose recordings count: everyone's, or only those of the shown departments' members.
        var owners = shown?.SelectMany(d => users.Values.Where(d.Has)).Select(u => u.Id).ToHashSet();
        var allItems = await stores.Items.ListAsync();
        var superseded = allItems.Where(i => i.SupersedesItemId is not null).Select(i => i.SupersedesItemId!.Value).ToHashSet();
        var items = allItems.Where(i => (only is null || i.Mode == only) && (owners is null || owners.Contains(i.OwnerId))).ToDictionary(i => i.Id);
        var events = await AllEventsAsync();
        var lastEvent = new Dictionary<Guid, DateTimeOffset>();
        foreach (var e in events) if (e.ItemId is { } iid && Steps.Contains(e.Type)) lastEvent[iid] = e.At;

        string Name(Guid id) => users.TryGetValue(id, out var u) ? u.Name : "Someone no longer here";
        var rows = new Dictionary<Guid, Counts>();
        Counts Row(Guid id) => rows.TryGetValue(id, out var r) ? r : rows[id] = new Counts();

        // What each person has now.
        var open = new List<(Item Item, Guid Holder, string Stage)>();
        foreach (var i in items.Values)
        {
            if (superseded.Contains(i.Id) || i.Status is ItemStatus.Completed or ItemStatus.Purged or ItemStatus.Draft) continue;
            var withAssignee = i.Status is ItemStatus.AwaitingAssignee or ItemStatus.WithAssignee && i.AssignedUserId is not null;
            var holder = withAssignee ? i.AssignedUserId!.Value : i.OwnerId;
            var stage = i.Status switch
            {
                ItemStatus.AwaitingAssignee when withAssignee => "To accept",
                ItemStatus.WithAssignee when withAssignee => "Being typed",
                ItemStatus.ConversionFailed => "Could not be converted",
                _ => "With the author",
            };
            open.Add((i, holder, stage));
            var r = Row(holder);
            if (stage == "To accept") r.ToAccept++; else if (stage == "Being typed") r.BeingTyped++; else r.AsAuthor++;
            if (i.DueDate is { } due) { if (due < today) r.Overdue++; else if (due == today) r.DueToday++; }
        }

        // What was done in the period, and how long each step took, from the History.
        var assignedAt = new Dictionary<(Guid Item, Guid To), DateTimeOffset>();
        var acceptedAt = new Dictionary<(Guid Item, Guid By), DateTimeOffset>();
        var readyAt = new Dictionary<Guid, DateTimeOffset>();
        var toApprove = new List<double>();
        foreach (var e in events)
        {
            if (e.ItemId is not { } id || !items.ContainsKey(id)) continue;
            var inPeriod = e.At >= from;
            switch (e.Type)
            {
                case "item.assigned" when GuidOf(e.Details, "to") is { } to && to != items[id].OwnerId:
                    assignedAt[(id, to)] = e.At;
                    if (inPeriod) Row(to).AssignedToThem++;
                    break;
                case "item.opened_by_assignee" when e.ActorId is { } by:
                    if (assignedAt.Remove((id, by), out var a) && inPeriod) Row(by).Accept.Add(Hours(a, e.At));
                    acceptedAt[(id, by)] = e.At;
                    break;
                case "item.returned_to_owner" when e.ActorId is { } by:
                    if (inPeriod) Row(by).Returned++;
                    if (acceptedAt.Remove((id, by), out var acc) && inPeriod) Row(by).Typing.Add(Hours(acc, e.At));
                    readyAt[id] = e.At;
                    break;
                case "processing.completed":
                    readyAt[id] = e.At;
                    break;
                case "item.approved" when e.ActorId is { } by:
                    if (!inPeriod) break;
                    Row(by).Approved++;
                    if (readyAt.Remove(id, out var ready)) Row(by).Approve.Add(Hours(ready, e.At));
                    toApprove.Add((e.At - items[id].CreatedAt).TotalDays);
                    break;
            }
        }

        var people = rows.Where(kv => users.ContainsKey(kv.Key) || kv.Value.Open > 0)
            .Select(kv => new WorkloadPerson(kv.Key, Name(kv.Key), kv.Value.AsAuthor, kv.Value.ToAccept, kv.Value.BeingTyped,
                kv.Value.Overdue, kv.Value.DueToday, kv.Value.AssignedToThem, kv.Value.Returned, kv.Value.Approved,
                Median(kv.Value.Accept), Median(kv.Value.Typing), Median(kv.Value.Approve)))
            .OrderByDescending(p => p.Overdue).ThenByDescending(p => p.AsAuthor + p.ToAccept + p.BeingTyped).ThenBy(p => p.Name)
            .ToList();

        WorkloadItem View((Item Item, Guid Holder, string Stage) o) => new(o.Item.Id, string.IsNullOrWhiteSpace(o.Item.Name) ? "(no name)" : o.Item.Name,
            o.Item.Mode.ToString(), Name(o.Holder), o.Stage, o.Item.DueDate,
            o.Item.DueDate is { } due && due < today ? today.DayNumber - due.DayNumber : null,
            lastEvent.TryGetValue(o.Item.Id, out var at) ? at : o.Item.CreatedAt);
        var overdue = open.Where(o => o.Item.DueDate is { } due && due < today).Select(View)
            .OrderByDescending(x => x.DaysLate).ThenBy(x => x.Name).ToList();
        var longest = open.Select(View).OrderBy(x => x.Since).Take(10).ToList();

        return new(period, only?.ToString().ToLowerInvariant() ?? "all", open.Count,
            open.Count(o => o.Item.DueDate is { } due && due < today), open.Count(o => o.Item.DueDate == today),
            toApprove.Count, Median(toApprove), people, overdue, longest, chosen?.Id,
            mine.OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).Select(d => new PersonOption(d.Id, d.Name)).ToList(), actor.IsAdmin);
    }

    private sealed class Counts
    {
        public int AsAuthor, ToAccept, BeingTyped, Overdue, DueToday, AssignedToThem, Returned, Approved;
        public List<double> Accept { get; } = [];
        public List<double> Typing { get; } = [];
        public List<double> Approve { get; } = [];
        public int Open => AsAuthor + ToAccept + BeingTyped;
    }

    private async Task<List<AuditEvent>> AllEventsAsync()
    {
        var all = new List<AuditEvent>();
        long after = 0;
        while (true)
        {
            var batch = await stores.Audit.ListAfterAsync(after, 2000);
            all.AddRange(batch);
            if (batch.Count < 2000) return all;
            after = batch[^1].Seq;
        }
    }

    private static double Hours(DateTimeOffset a, DateTimeOffset b) => Math.Max(0, (b - a).TotalHours);

    internal static double? Median(List<double> values)
    {
        if (values.Count == 0) return null;
        var s = values.Order().ToList();
        var m = s.Count / 2;
        return Math.Round(s.Count % 2 == 1 ? s[m] : (s[m - 1] + s[m]) / 2, 1);
    }

    private static Guid? GuidOf(string details, string key)
    {
        try
        {
            using var doc = JsonDocument.Parse(details);
            return doc.RootElement.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String && Guid.TryParse(v.GetString(), out var g) ? g : null;
        }
        catch (JsonException) { return null; }
    }
}
