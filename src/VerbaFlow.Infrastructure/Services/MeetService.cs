using System.Collections.Concurrent;
using VerbaFlow.Core.Audit;
using VerbaFlow.Core.Domain;
using VerbaFlow.Core.Providers;

namespace VerbaFlow.Infrastructure.Services;

public sealed record NoteTakerBooking(string? MeetingUrl, string? Name, DateTimeOffset? JoinAt, string? SpokenLanguages);

/// <param name="JoinedExisting">A colleague had already booked the note-taker for this meeting, so this person was added to that booking.</param>
public sealed record NoteTakerBooked(NoteTakerRow NoteTaker, bool JoinedExisting, bool AlreadyBooked);

public sealed record NoteTakerRow(Guid Id, string Name, string Platform, DateTimeOffset JoinAt, string State, string StateLabel,
    string? FailureReason, Guid? ItemId, string BookedBy, IReadOnlyList<string> AlsoFor, bool YouBookedFirst, bool YouAsked, bool CanCancel, DateTimeOffset CreatedAt);

public sealed record MeetOverview(string Engine, bool Real, string NoteTakerName, IReadOnlyList<string> Platforms, IReadOnlyList<NoteTakerRow> NoteTakers);

/// <summary>
/// Meet: a note-taker joins an online meeting from its link (Zoom, Google Meet, Microsoft Teams, Webex), records it as a
/// visible participant, and its video comes back as a Meeting item. Only one note-taker joins a meeting: when a colleague
/// books the same meeting, they are added to the existing booking. The first person to book owns the item; the others
/// can read it (and export it once approved).
/// </summary>
public sealed class MeetService(Stores stores, INoteTakerService service, MeetingService meetings, TimeProvider clock,
    FailureReporter reporter, string noteTakerName = "VerbaFlow Notetaker")
{
    private const string Product = "meet";
    /// <summary>Two bookings of the same meeting link closer together than this are the same meeting (a weekly meeting can reuse its link).</summary>
    public static readonly TimeSpan SameMeetingWindow = TimeSpan.FromHours(3);
    /// <summary>A note-taker that has still not got into its meeting this long after the start is given up on.</summary>
    public static readonly TimeSpan GiveUpAfter = TimeSpan.FromHours(6);
    /// <summary>After this many failed checks or downloads in a row, the note-taker is marked failed.</summary>
    public const int MaxFailuresInARow = 20;

    public string NoteTakerName => noteTakerName;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly ConcurrentDictionary<Guid, int> failuresInARow = new();

    public async Task<MeetOverview> OverviewAsync(User actor) =>
        new(service.Name, service.Name != StandIns.StandInNoteTaker.EngineName, noteTakerName,
            [MeetingPlatforms.Zoom, MeetingPlatforms.GoogleMeet, MeetingPlatforms.Teams, MeetingPlatforms.Webex], await ListAsync(actor));

    /// <summary>Sends the note-taker to a meeting, or adds this person to a colleague's booking of the same meeting.</summary>
    /// <param name="via">"page" when the person booked it, "calendar" when their calendar did (recorded in History).</param>
    public async Task<NoteTakerBooked> BookAsync(User actor, NoteTakerBooking b, CancellationToken ct = default, string via = "page")
    {
        var url = (b.MeetingUrl ?? "").Trim();
        var platform = MeetingPlatforms.Of(url)
            ?? throw new DomainException("Paste the meeting's join link (it starts with https://). The note-taker can join Zoom, Google Meet, Microsoft Teams and Webex meetings.");
        var key = MeetingPlatforms.Key(url)!;
        var now = clock.GetUtcNow();
        var joinAt = b.JoinAt ?? now;
        if (joinAt < now - TimeSpan.FromMinutes(5)) throw new DomainException("That time has already passed. Choose when the meeting starts, or send the note-taker now.");
        if (joinAt > now.AddDays(366)) throw new DomainException("The note-taker can be booked up to a year ahead.");
        if (joinAt < now) joinAt = now;
        var languages = NormaliseLanguages(b.SpokenLanguages);
        var name = string.IsNullOrWhiteSpace(b.Name) ? $"{platform} meeting {joinAt:d MMM yyyy}" : b.Name.Trim();
        if (name.Length > 200) throw new DomainException("The name is too long.");

        await gate.WaitAsync(ct);
        try
        {
            var same = (await stores.NoteTakers.ListAsync())
                .Where(n => n.IsActive && n.MeetingKey == key && (n.JoinAt - joinAt).Duration() <= SameMeetingWindow)
                .OrderBy(n => n.CreatedAt).FirstOrDefault();
            if (same is not null)
            {
                if (same.Requesters.Contains(actor.Id)) return new(await RowAsync(actor, same), false, true);
                var joined = same with { Requesters = [.. same.Requesters, actor.Id] };
                await stores.NoteTakers.UpsertAsync(same.Id, joined);
                await Audit(actor, "user", "meet.joined_booking", ("noteTaker", same.Id), ("platform", platform), ("requesters", joined.Requesters.Count), ("via", via));
                return new(await RowAsync(actor, joined), true, false);
            }

            var id = Guid.NewGuid();
            var announcement = $"{noteTakerName} is recording this meeting for {actor.Name}, so that notes can be made. If you do not agree to being recorded, please tell the host.";
            string botId;
            try { botId = await service.ScheduleAsync(new NoteTakerRequest(url, noteTakerName, joinAt, announcement, id), ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var rep = await reporter.ReportAsync("note-taker", null, ex);
                await Audit(actor, "user", "meet.booking_failed", ("platform", platform), ("reference", rep.Reference), ("via", via));
                throw new DomainException($"The note-taker could not be booked. Try again in a few minutes, and contact support if it happens again. Reference: {rep.Reference}.");
            }
            var nt = new NoteTaker(id, [actor.Id], name, url, key, platform, joinAt, languages, now, NoteTakerState.Scheduled, botId, now);
            await stores.NoteTakers.UpsertAsync(id, nt);
            await Audit(actor, "user", "meet.booked", ("noteTaker", id), ("platform", platform), ("joinAt", joinAt), ("languages", languages), ("engine", service.Name), ("via", via));
            return new(await RowAsync(actor, nt), false, false);
        }
        finally { gate.Release(); }
    }

    /// <summary>
    /// "I don't need it": the person comes off the booking. The note-taker is called off only when nobody else asked for
    /// it; otherwise the next person who asked becomes the owner.
    /// </summary>
    public async Task<NoteTakerRow> CancelAsync(User actor, Guid id, CancellationToken ct = default, string via = "page")
    {
        await gate.WaitAsync(ct);
        try
        {
            var nt = await stores.NoteTakers.GetAsync(id) ?? throw new NotFoundException("That note-taker booking does not exist.");
            if (!nt.Requesters.Contains(actor.Id) && !actor.IsAdmin) throw new ForbiddenException("Only someone who asked for this note-taker, or an administrator, can call it off.");
            if (!nt.IsActive) throw new DomainException("This note-taker has already finished.");
            if (nt.Requesters.Contains(actor.Id) && nt.Requesters.Count > 1)
            {
                var left = nt with { Requesters = nt.Requesters.Where(r => r != actor.Id).ToList() };
                await stores.NoteTakers.UpsertAsync(id, left);
                await Audit(actor, "user", "meet.left_booking", ("noteTaker", id), ("requesters", left.Requesters.Count), ("via", via));
                return await RowAsync(actor, left);
            }
            try { if (nt.BotId is not null) await service.CancelAsync(nt.BotId, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var rep = await reporter.ReportAsync("note-taker", null, ex);
                throw new DomainException($"The note-taker could not be called off. Try again in a few minutes, and contact support if it happens again. Reference: {rep.Reference}.");
            }
            var cancelled = nt with { State = NoteTakerState.Cancelled, StateAt = clock.GetUtcNow() };
            await stores.NoteTakers.UpsertAsync(id, cancelled);
            await Audit(actor, actor.IsAdmin && !nt.Requesters.Contains(actor.Id) ? "admin" : "user", "meet.cancelled", ("noteTaker", id), ("platform", nt.Platform), ("via", via));
            return await RowAsync(actor, cancelled);
        }
        finally { gate.Release(); }
    }

    /// <summary>The note-takers this person asked for (administrators see everyone's), newest first.</summary>
    public async Task<IReadOnlyList<NoteTakerRow>> ListAsync(User actor)
    {
        var users = (await stores.Users.ListAsync()).ToDictionary(u => u.Id, u => u.Name);
        return (await stores.NoteTakers.ListAsync()).Where(n => actor.IsAdmin || n.Requesters.Contains(actor.Id))
            .OrderByDescending(n => n.JoinAt).Take(200).Select(n => Row(actor, n, users)).ToList();
    }

    /// <summary>
    /// Asks the note-taker service how each note-taker is getting on, and brings back the video of every meeting that has
    /// ended. Run every minute by the Meet worker, and by "Check now".
    /// </summary>
    public async Task<int> CheckAsync(CancellationToken ct = default)
    {
        var fetched = 0;
        var now = clock.GetUtcNow();
        foreach (var nt in (await stores.NoteTakers.ListAsync()).Where(n => !n.IsFinished && n.BotId is not null))
        {
            ct.ThrowIfCancellationRequested();
            // Nothing to ask about a meeting that has not started yet.
            if (nt.State == NoteTakerState.Scheduled && nt.JoinAt - now > TimeSpan.FromMinutes(2)) continue;
            NoteTakerStatus status;
            try { status = await service.GetAsync(nt.BotId!, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { await FailedTryAsync(nt, ex, "The note-taker service could not be reached"); continue; }
            failuresInARow.TryRemove(nt.Id, out _);

            if (status.Phase == NoteTakerPhase.Ready) { if (await FetchAsync(nt, status, ct)) fetched++; continue; }
            if (status.Phase == NoteTakerPhase.Failed)
            {
                var rep = await reporter.ReportAsync("note-taker", null, new ProviderException(FaultKind.Other, $"Note-taker {nt.BotId} failed: {status.Detail}"));
                await SetStateAsync(nt, NoteTakerState.Failed, NotRecordedReason(status.Detail, rep.Reference));
                continue;
            }
            var state = status.Phase switch
            {
                NoteTakerPhase.Joining => NoteTakerState.Joining,
                NoteTakerPhase.WaitingToBeLetIn => NoteTakerState.WaitingToBeLetIn,
                NoteTakerPhase.Recording => NoteTakerState.Recording,
                NoteTakerPhase.MeetingEnded => NoteTakerState.MeetingEnded,
                _ => NoteTakerState.Scheduled,
            };
            if (state is NoteTakerState.Scheduled or NoteTakerState.Joining or NoteTakerState.WaitingToBeLetIn && now - nt.JoinAt > GiveUpAfter)
            {
                try { await service.CancelAsync(nt.BotId!, ct); } catch (Exception ex) when (ex is not OperationCanceledException) { await reporter.ReportAsync("note-taker", null, ex); }
                await SetStateAsync(nt, NoteTakerState.Failed, "The note-taker never got into the meeting. If the meeting has a lobby, the host needs to let it in.");
                continue;
            }
            if (state != nt.State) await SetStateAsync(nt, state, null);
        }
        return fetched;
    }

    private static string NotRecordedReason(string? detail, string reference)
    {
        var d = detail ?? "";
        var why = d.Contains("waiting_room") ? "The host did not let the note-taker in from the lobby."
            : d.Contains("permission_denied") || d.Contains("recording_permission") ? "The host did not allow the note-taker to record."
            : d.Contains("noone_joined") || d.Contains("no_one") ? "Nobody else joined the meeting."
            : d.Contains("no recording") ? "The note-taker did not record anything."
            : "The note-taker could not record this meeting.";
        return $"{why} Reference: {reference}.";
    }

    private async Task<bool> FetchAsync(NoteTaker nt, NoteTakerStatus status, CancellationToken ct)
    {
        // A recording already brought back (for example the app stopped just after) is not brought back twice.
        if (nt.ItemId is not null) { await SetStateAsync(nt, NoteTakerState.Fetched, null); return false; }
        IReadOnlyList<PlatformSpeech> speakers = [];
        if (status.SpeakersUrl is not null)
            try { speakers = await service.DownloadSpeakersAsync(status.SpeakersUrl, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { await reporter.ReportAsync("note-taker", null, ex); }
        Guid itemId;
        try
        {
            await using var video = await service.DownloadVideoAsync(status.VideoUrl!, ct);
            var sample = service is StandIns.StandInNoteTaker s && !s.MakesVideo;
            var (file, type) = sample ? ("meeting.wav", "audio/wav") : ("meeting.mp4", "video/mp4");
            itemId = await meetings.CreateFromOnlineMeetingAsync(new OnlineMeetingUpload(video, file, type, nt.Name, nt.Requesters, nt.Platform,
                noteTakerName, nt.Id, nt.SpokenLanguages, speakers), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { await FailedTryAsync(nt, ex, "The recording could not be brought back from the note-taker"); return false; }
        failuresInARow.TryRemove(nt.Id, out _);
        var done = nt with { State = NoteTakerState.Fetched, StateAt = clock.GetUtcNow(), ItemId = itemId, FailureReason = null };
        await stores.NoteTakers.UpsertAsync(nt.Id, done);
        await stores.Audit.AppendAsync(null, "system", Product, itemId, "meet.recording_fetched",
            AuditChain.Details(("noteTaker", nt.Id), ("platform", nt.Platform), ("platformSpeakers", speakers.Select(x => x.Participant).Distinct().Count())));
        // VerbaFlow keeps the recording now; the copy at the note-taker service is removed.
        try { await service.ForgetAsync(nt.BotId!, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var rep = await reporter.ReportAsync("note-taker", itemId, ex);
            await stores.Audit.AppendAsync(null, "system", Product, itemId, "meet.service_copy_not_removed", AuditChain.Details(("reference", rep.Reference)));
        }
        return true;
    }

    private async Task FailedTryAsync(NoteTaker nt, Exception ex, string what)
    {
        var count = failuresInARow.AddOrUpdate(nt.Id, 1, (_, c) => c + 1);
        if (count < MaxFailuresInARow) return;
        failuresInARow.TryRemove(nt.Id, out _);
        var rep = await reporter.ReportAsync("note-taker", null, ex);
        await SetStateAsync(nt, NoteTakerState.Failed, $"{what}. Contact support. Reference: {rep.Reference}.");
    }

    private async Task SetStateAsync(NoteTaker nt, NoteTakerState state, string? reason)
    {
        var current = await stores.NoteTakers.GetAsync(nt.Id) ?? nt; // a colleague may have joined the booking meanwhile
        if (current.IsFinished) return;
        await stores.NoteTakers.UpsertAsync(nt.Id, current with { State = state, StateAt = clock.GetUtcNow(), FailureReason = reason });
        await stores.Audit.AppendAsync(null, "system", Product, null, "meet.state_changed",
            AuditChain.Details(("noteTaker", nt.Id), ("state", state.ToString()), ("reason", reason)));
    }

    public static string LabelOf(NoteTakerState s) => s switch
    {
        NoteTakerState.Scheduled => "Booked",
        NoteTakerState.Joining => "Joining the meeting",
        NoteTakerState.WaitingToBeLetIn => "Waiting to be let in",
        NoteTakerState.Recording => "Recording",
        NoteTakerState.MeetingEnded => "Meeting ended, bringing back the recording",
        NoteTakerState.Fetched => "Recording ready",
        NoteTakerState.Failed => "Not recorded",
        _ => "Called off",
    };

    private async Task<NoteTakerRow> RowAsync(User actor, NoteTaker n) =>
        Row(actor, n, (await stores.Users.ListAsync()).ToDictionary(u => u.Id, u => u.Name));

    private static NoteTakerRow Row(User actor, NoteTaker n, Dictionary<Guid, string> users) =>
        new(n.Id, n.Name, n.Platform, n.JoinAt, n.State.ToString(), LabelOf(n.State), n.FailureReason, n.ItemId,
            users.GetValueOrDefault(n.OwnerId, "?"), n.Requesters.Skip(1).Select(r => users.GetValueOrDefault(r, "?")).ToList(),
            n.OwnerId == actor.Id, n.Requesters.Contains(actor.Id), n.IsActive && (n.Requesters.Contains(actor.Id) || actor.IsAdmin), n.CreatedAt);

    /// <summary>Up to three known spoken languages, comma-separated, or "auto".</summary>
    public static string NormaliseLanguages(string? value)
    {
        var parts = (value ?? "").Split([',', '+'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(SpokenLanguageCatalogue.Normalise).Where(x => x is not null).Select(x => x!).Distinct()
            .Take(SpokenLanguageCatalogue.MaxPerRecording).ToList();
        return parts.Contains("auto") ? "auto" : parts.Count == 0 ? "auto" : string.Join(",", parts);
    }

    private Task Audit(User actor, string capacity, string type, params (string, object?)[] details) =>
        stores.Audit.AppendAsync(actor.Id, capacity, Product, null, type, AuditChain.Details(details));
}
