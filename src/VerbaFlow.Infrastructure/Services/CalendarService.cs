using System.Collections.Concurrent;
using VerbaFlow.Core.Audit;
using VerbaFlow.Core.Domain;
using VerbaFlow.Core.Providers;
using VerbaFlow.Infrastructure.Calendars;

namespace VerbaFlow.Infrastructure.Services;

public sealed record CalendarConnect(string? Source, string? Link, bool? AutoSend, string? SpokenLanguages);
public sealed record CalendarSwitch(string? EventKey, bool Send);

public sealed record CalendarSourceOption(string Id, string Label);

/// <param name="Send">Whether the note-taker goes to this meeting, after the person's own choice and the automatic setting.</param>
/// <param name="Choice">The person's own choice for this meeting; null when it follows the automatic setting.</param>
/// <param name="Why">A plain sentence when the note-taker will not go although the meeting is switched on (e.g. declined).</param>
public sealed record CalendarMeetingRow(string EventKey, string Subject, DateTimeOffset Start, DateTimeOffset End, string? Platform,
    bool Send, bool? Choice, string? Why, Guid? NoteTakerId, string? NoteTakerState, string? NoteTakerLabel);

/// <param name="Connected">Null when no calendar is connected.</param>
/// <param name="Shown">For a calendar address: the site only (the address works like a password).</param>
public sealed record CalendarView(string Source, string SourceLabel, string? Shown, bool AutoSend, string SpokenLanguages,
    DateTimeOffset ConnectedAt, DateTimeOffset? LastReadAt, string? Problem);

public sealed record CalendarOverview(IReadOnlyList<CalendarSourceOption> Sources, CalendarView? Connected,
    IReadOnlyList<CalendarMeetingRow> Meetings, int OtherMeetings, int BookAheadMinutes);

/// <summary>
/// Meet: the note-taker goes to the online meetings in a person's calendar. The calendar is read every few minutes;
/// a meeting with a Zoom, Google Meet, Teams or Webex link gets the note-taker booked an hour before it starts (when the
/// person has automatic booking on, or switched that meeting on). Booking goes through MeetService, so a meeting in
/// several colleagues' calendars still gets one note-taker. A meeting that is switched off, declined, cancelled or moved
/// has the person taken off its booking again (the note-taker is called off only when nobody else needs it).
/// </summary>
public sealed class CalendarService(Stores stores, IEnumerable<ICalendarReader> readers, MeetService meet, TimeProvider clock,
    FailureReporter reporter)
{
    private const string Product = "meet";
    /// <summary>The note-taker is booked this long before a meeting starts.</summary>
    public static readonly TimeSpan BookAhead = TimeSpan.FromMinutes(60);
    /// <summary>A meeting that started up to this long ago still gets the note-taker (e.g. switched on during it).</summary>
    public static readonly TimeSpan LateStart = TimeSpan.FromMinutes(15);
    /// <summary>How far ahead the Meet page shows meetings.</summary>
    public static readonly TimeSpan ShowAhead = TimeSpan.FromDays(7);
    private static readonly TimeSpan ReadAgainAfter = TimeSpan.FromSeconds(60);

    private readonly Dictionary<string, ICalendarReader> bySource = readers.ToDictionary(r => r.Source);
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly ConcurrentDictionary<Guid, (DateTimeOffset At, IReadOnlyList<CalendarMeeting> Meetings)> recent = new();

    /// <summary>The calendar sources this person can connect.</summary>
    public IReadOnlyList<CalendarSourceOption> SourcesFor(User user)
    {
        var list = new List<CalendarSourceOption>();
        if (bySource.ContainsKey(CalendarSources.Microsoft) && !string.IsNullOrEmpty(user.EntraObjectId)) list.Add(Option(CalendarSources.Microsoft));
        if (bySource.ContainsKey(CalendarSources.Link)) list.Add(Option(CalendarSources.Link));
        if (bySource.ContainsKey(CalendarSources.Sample)) list.Add(Option(CalendarSources.Sample));
        return list;
    }

    private static CalendarSourceOption Option(string id) => new(id, CalendarSources.Label(id));

    public async Task<CalendarOverview> OverviewAsync(User user, CancellationToken ct = default)
    {
        var settings = await stores.CalendarSettings.GetAsync(user.Id);
        if (settings is null) return new(SourcesFor(user), null, [], 0, (int)BookAhead.TotalMinutes);
        var now = clock.GetUtcNow();
        IReadOnlyList<CalendarMeeting> meetings = [];
        if (recent.TryGetValue(user.Id, out var r) && now - r.At < ReadAgainAfter) meetings = r.Meetings;
        else
        {
            (settings, meetings) = await ReadAsync(user, settings, ct);
        }
        var choices = (await stores.CalendarChoices.ListAsync()).Where(c => c.UserId == user.Id).ToDictionary(c => c.EventKey);
        var noteTakers = (await stores.NoteTakers.ListAsync()).ToDictionary(n => n.Id);
        var rows = meetings.Where(m => m.JoinUrl is not null && m.End > now).OrderBy(m => m.Start).Take(100).Select(m =>
        {
            choices.TryGetValue(m.EventKey, out var c);
            var send = (c?.Send ?? settings.AutoSend) && !m.Declined;
            var nt = c?.NoteTakerId is { } id && noteTakers.TryGetValue(id, out var n) && n.Requesters.Contains(user.Id) ? n : null;
            var why = (c?.Send ?? settings.AutoSend) && m.Declined ? "You declined this meeting." : null;
            return new CalendarMeetingRow(m.EventKey, m.Subject, m.Start, m.End, MeetingPlatforms.Of(m.JoinUrl), send, c?.Send, why,
                nt?.Id, nt?.State.ToString(), nt is null ? null : MeetService.LabelOf(nt.State));
        }).ToList();
        var others = meetings.Count(m => m.JoinUrl is null && m.End > now);
        return new(SourcesFor(user), View(settings), rows, others, (int)BookAhead.TotalMinutes);
    }

    private static CalendarView View(CalendarSettings s) =>
        new(s.Source, CalendarSources.Label(s.Source), s.Source == CalendarSources.Link && s.Link is not null ? LinkCalendarReader.Masked(s.Link) : null,
            s.AutoSend, s.SpokenLanguages, s.ConnectedAt, s.LastReadAt, s.Problem);

    /// <summary>Connects a calendar, or changes the automatic setting and language of the one connected.</summary>
    public async Task<CalendarOverview> ConnectAsync(User user, CalendarConnect body, CancellationToken ct = default)
    {
        var existing = await stores.CalendarSettings.GetAsync(user.Id);
        var source = (body.Source ?? existing?.Source ?? "").Trim().ToLowerInvariant();
        if (!SourcesFor(user).Any(o => o.Id == source)) throw new DomainException("Choose which calendar to connect.");
        var languages = MeetService.NormaliseLanguages(body.SpokenLanguages ?? existing?.SpokenLanguages);
        var autoSend = body.AutoSend ?? existing?.AutoSend ?? true;
        var now = clock.GetUtcNow();
        var changingSource = existing is null || existing.Source != source || (source == CalendarSources.Link && body.Link is { Length: > 0 });

        CalendarSettings settings;
        if (changingSource)
        {
            string? link = null;
            if (source == CalendarSources.Link)
                link = LinkCalendarReader.Normalise(body.Link)
                    ?? throw new DomainException("Paste your calendar's private address. It starts with https:// or webcal:// (in Outlook.com it is the ICS link under Shared calendars, in Google Calendar the \"Secret address in iCal format\").");
            if (existing is not null) await ForgetBookingsAsync(user, existing, ct);
            settings = new CalendarSettings(user.Id, source, link, autoSend, languages, now);
            // Read it once now, so a wrong address or a missing permission is said straight away.
            try { await bySource[source].ReadAsync(settings, user, now, now.AddDays(1), ct); }
            catch (ProviderException ex)
            {
                var rep = await reporter.ReportAsync("calendar", null, ex);
                throw new DomainException($"{CouldNotRead(source, FaultKind.Settings)} Reference: {rep.Reference}.");
            }
            await stores.CalendarSettings.UpsertAsync(user.Id, settings);
            recent.TryRemove(user.Id, out _);
            await Audit(user, "calendar.connected", ("source", source), ("autoSend", autoSend), ("languages", languages));
        }
        else
        {
            settings = existing! with { AutoSend = autoSend, SpokenLanguages = languages };
            await stores.CalendarSettings.UpsertAsync(user.Id, settings);
            if (existing.AutoSend != autoSend || existing.SpokenLanguages != languages)
                await Audit(user, "calendar.settings_changed", ("autoSend", autoSend), ("languages", languages));
        }
        await SyncUserAsync(user, settings, ct);
        return await OverviewAsync(user, ct);
    }

    /// <summary>Disconnects the calendar: the person comes off every booking the calendar made, and their choices are forgotten.</summary>
    public async Task<CalendarOverview> DisconnectAsync(User user, CancellationToken ct = default)
    {
        var existing = await stores.CalendarSettings.GetAsync(user.Id) ?? throw new DomainException("No calendar is connected.");
        await ForgetBookingsAsync(user, existing, ct);
        await stores.CalendarSettings.DeleteAsync(user.Id);
        recent.TryRemove(user.Id, out _);
        await Audit(user, "calendar.disconnected", ("source", existing.Source));
        return await OverviewAsync(user, ct);
    }

    /// <summary>Switches the note-taker on or off for one meeting in the person's calendar.</summary>
    public async Task<CalendarOverview> SwitchAsync(User user, CalendarSwitch body, CancellationToken ct = default)
    {
        var settings = await stores.CalendarSettings.GetAsync(user.Id) ?? throw new DomainException("Connect your calendar first.");
        var key = body.EventKey ?? "";
        var (_, meetings) = recent.TryGetValue(user.Id, out var r) ? (settings, r.Meetings) : await ReadAsync(user, settings, ct);
        var m = meetings.FirstOrDefault(x => x.EventKey == key && x.JoinUrl is not null)
            ?? throw new NotFoundException("That meeting is no longer in your calendar. Refresh the page.");
        await gate.WaitAsync(ct);
        try
        {
            var id = CalendarChoice.IdFor(user.Id, key);
            var c = await stores.CalendarChoices.GetAsync(id) ?? new CalendarChoice(id, user.Id, key, null, m.Start, m.End);
            c = c with { Send = body.Send, Start = m.Start, End = m.End };
            if (!body.Send) c = await TakeOffAsync(user, c, ct);
            await stores.CalendarChoices.UpsertAsync(id, c);
        }
        finally { gate.Release(); }
        await Audit(user, "calendar.meeting_switched", ("send", body.Send), ("platform", MeetingPlatforms.Of(m.JoinUrl)), ("start", m.Start));
        await SyncUserAsync(user, settings, ct);
        return await OverviewAsync(user, ct);
    }

    /// <summary>Reads every connected calendar and books or calls off note-takers. Run every few minutes by the worker.</summary>
    public async Task SyncAsync(CancellationToken ct = default)
    {
        foreach (var settings in await stores.CalendarSettings.ListAsync())
        {
            ct.ThrowIfCancellationRequested();
            var user = await stores.Users.GetAsync(settings.UserId);
            if (user is null) continue;
            try { await SyncUserAsync(user, settings, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { await reporter.ReportAsync("calendar", null, ex); }
        }
    }

    private async Task SyncUserAsync(User user, CalendarSettings settings, CancellationToken ct)
    {
        var (fresh, meetings) = await ReadAsync(user, settings, ct, force: true);
        if (fresh.Problem is not null) return; // nothing is called off on the strength of a calendar that could not be read
        var now = clock.GetUtcNow();
        await gate.WaitAsync(ct);
        try
        {
            var choices = (await stores.CalendarChoices.ListAsync()).Where(c => c.UserId == user.Id).ToDictionary(c => c.EventKey);
            var seen = new HashSet<string>();
            foreach (var m in meetings.Where(m => m.JoinUrl is not null))
            {
                seen.Add(m.EventKey);
                choices.TryGetValue(m.EventKey, out var c);
                var send = (c?.Send ?? settings.AutoSend) && !m.Declined;
                var current = c?.NoteTakerId is { } ntId ? await stores.NoteTakers.GetAsync(ntId) : null;

                // Switched off, declined, or moved since it was booked: come off that booking.
                if (c is not null && current is not null && c.BookedHere && current.IsActive && current.Requesters.Contains(user.Id)
                    && (!send || (c.BookedStart is { } bs && (bs - m.Start).Duration() > TimeSpan.FromMinutes(2) && current.State == NoteTakerState.Scheduled)))
                {
                    c = await TakeOffAsync(user, c, ct);
                    await stores.CalendarChoices.UpsertAsync(c.Id, c);
                    current = null;
                }
                if (!send || current is not null) continue;
                if (m.Start - now > BookAhead || m.End <= now || now - m.Start > LateStart) continue;

                NoteTakerBooked booked;
                try { booked = await meet.BookAsync(user, new NoteTakerBooking(m.JoinUrl, m.Subject, m.Start < now ? null : m.Start, settings.SpokenLanguages), ct, via: "calendar"); }
                catch (DomainException) { continue; } // e.g. the booking service refused; already reported, tried again next time
                var id = CalendarChoice.IdFor(user.Id, m.EventKey);
                await stores.CalendarChoices.UpsertAsync(id, (c ?? new CalendarChoice(id, user.Id, m.EventKey, null, m.Start, m.End)) with
                {
                    Start = m.Start, End = m.End, NoteTakerId = booked.NoteTaker.Id, BookedHere = !booked.AlreadyBooked, BookedStart = m.Start,
                });
            }

            foreach (var c in choices.Values.Where(c => !seen.Contains(c.EventKey)))
            {
                // Gone from the calendar (cancelled or deleted) before it started: come off the booking.
                if (c.NoteTakerId is not null && c.BookedHere && c.Start > now)
                {
                    var nt = await stores.NoteTakers.GetAsync(c.NoteTakerId.Value);
                    if (nt is { IsActive: true, State: NoteTakerState.Scheduled } && nt.Requesters.Contains(user.Id))
                        await stores.CalendarChoices.UpsertAsync(c.Id, await TakeOffAsync(user, c, ct));
                }
                // Choices for meetings long over are forgotten.
                if (c.End < now.AddDays(-2)) await stores.CalendarChoices.DeleteAsync(c.Id);
            }
        }
        finally { gate.Release(); }
    }

    /// <summary>Takes the person off the note-taker the calendar booked for them (calls it off when nobody else needs it).</summary>
    private async Task<CalendarChoice> TakeOffAsync(User user, CalendarChoice c, CancellationToken ct)
    {
        if (c.NoteTakerId is { } id && c.BookedHere && await stores.NoteTakers.GetAsync(id) is { IsActive: true } nt && nt.Requesters.Contains(user.Id))
        {
            try { await meet.CancelAsync(user, id, ct, via: "calendar"); }
            catch (DomainException) { return c; } // could not be called off now; tried again next time
        }
        return c with { NoteTakerId = null, BookedHere = false, BookedStart = null };
    }

    private async Task ForgetBookingsAsync(User user, CalendarSettings settings, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var now = clock.GetUtcNow();
            foreach (var c in (await stores.CalendarChoices.ListAsync()).Where(c => c.UserId == user.Id))
            {
                if (c.NoteTakerId is { } id && await stores.NoteTakers.GetAsync(id) is { State: NoteTakerState.Scheduled } && c.Start > now)
                    await TakeOffAsync(user, c, ct);
                await stores.CalendarChoices.DeleteAsync(c.Id);
            }
        }
        finally { gate.Release(); }
    }

    /// <summary>Reads the calendar for the next week; on failure keeps the last meetings read and records a plain problem (reported once).</summary>
    private async Task<(CalendarSettings Settings, IReadOnlyList<CalendarMeeting> Meetings)> ReadAsync(User user, CalendarSettings settings, CancellationToken ct, bool force = false)
    {
        var now = clock.GetUtcNow();
        if (!force && recent.TryGetValue(user.Id, out var r) && now - r.At < ReadAgainAfter) return (settings, r.Meetings);
        if (!bySource.TryGetValue(settings.Source, out var reader))
            return (await SaveProblemAsync(settings, "This calendar can no longer be read here. Connect it again."), []);
        try
        {
            var meetings = await reader.ReadAsync(settings, user, now.AddHours(-12), now + ShowAhead, ct);
            recent[user.Id] = (now, meetings);
            var updated = settings with { LastReadAt = now, Problem = null };
            await stores.CalendarSettings.UpsertAsync(user.Id, updated);
            return (updated, meetings);
        }
        catch (ProviderException ex)
        {
            // Reported to support once, not every few minutes while it stays broken.
            if (settings.Problem is not null) return (settings, recent.TryGetValue(user.Id, out var last) ? last.Meetings : []);
            var rep = await reporter.ReportAsync("calendar", null, ex);
            var problem = $"{CouldNotRead(settings.Source, ex.Kind)} Reference: {rep.Reference}.";
            await Audit(user, "calendar.read_failed", ("source", settings.Source), ("reference", rep.Reference));
            return (await SaveProblemAsync(settings, problem), recent.TryGetValue(user.Id, out var l) ? l.Meetings : []);
        }
    }

    private async Task<CalendarSettings> SaveProblemAsync(CalendarSettings settings, string problem)
    {
        var updated = settings with { Problem = problem };
        await stores.CalendarSettings.UpsertAsync(settings.UserId, updated);
        return updated;
    }

    private static string CouldNotRead(string source, FaultKind kind) => (source, kind) switch
    {
        (CalendarSources.Microsoft, FaultKind.Settings) => "Your Microsoft 365 calendar could not be read. Your organisation may not have allowed VerbaFlow to read calendars yet; ask your administrator.",
        (CalendarSources.Microsoft, _) => "Your Microsoft 365 calendar could not be read just now. VerbaFlow will try again in a few minutes.",
        (_, FaultKind.Settings) => "Your calendar could not be read from that address. Check that you copied the whole private address, and that it has not been reset.",
        _ => "Your calendar could not be read from that address just now. VerbaFlow will try again in a few minutes.",
    };

    private Task Audit(User user, string type, params (string, object?)[] details) =>
        stores.Audit.AppendAsync(user.Id, "user", Product, null, type, AuditChain.Details(details));
}
