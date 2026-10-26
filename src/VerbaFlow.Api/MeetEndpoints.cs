using System.Collections.Concurrent;
using System.Security.Cryptography;
using VerbaFlow.Infrastructure.Services;

namespace VerbaFlow.Api;

/// <summary>Short-lived links that let the browser's video player stream a recording (it cannot send the sign-in header).</summary>
public sealed class PlaybackLinks(TimeProvider clock)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(8);
    private readonly ConcurrentDictionary<string, (Guid Item, DateTimeOffset Until)> links = new();

    public string Issue(Guid itemId)
    {
        var now = clock.GetUtcNow();
        foreach (var old in links.Where(l => l.Value.Until < now)) links.TryRemove(old.Key, out _);
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        links[token] = (itemId, now + Lifetime);
        return token;
    }

    public Guid? Resolve(string? token) =>
        token is not null && links.TryGetValue(token, out var l) && l.Until > clock.GetUtcNow() ? l.Item : null;
}

/// <summary>Meet: booking the note-taker for online meetings, and playing a recording through a link.</summary>
public static class MeetEndpoints
{
    public static void MapMeetEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/meet", (HttpContext h, MeetService m) => m.OverviewAsync(h.Current()));
        api.MapPost("/meet", (HttpContext h, MeetService m, NoteTakerBooking b, CancellationToken ct) => m.BookAsync(h.Current(), b, ct));
        api.MapPost("/meet/{id:guid}/cancel", (HttpContext h, MeetService m, Guid id, CancellationToken ct) => m.CancelAsync(h.Current(), id, ct));
        api.MapPost("/meet/check", async (HttpContext h, MeetService m, CancellationToken ct) =>
        {
            await m.CheckAsync(ct);
            return await m.OverviewAsync(h.Current());
        });
        api.MapGet("/meet/calendar", (HttpContext h, CalendarService c, CancellationToken ct) => c.OverviewAsync(h.Current(), ct));
        api.MapPut("/meet/calendar", (HttpContext h, CalendarService c, CalendarConnect b, CancellationToken ct) => c.ConnectAsync(h.Current(), b, ct));
        api.MapDelete("/meet/calendar", (HttpContext h, CalendarService c, CancellationToken ct) => c.DisconnectAsync(h.Current(), ct));
        api.MapPost("/meet/calendar/switch", (HttpContext h, CalendarService c, CalendarSwitch b, CancellationToken ct) => c.SwitchAsync(h.Current(), b, ct));
        api.MapPost("/items/{id:guid}/play", async (HttpContext h, MeetingService s, PlaybackLinks links, Guid id) =>
        {
            await s.StartPlaybackAsync(h.Current(), id);
            return Results.Ok(new { url = $"/api/play/{links.Issue(id)}" });
        });
    }

    /// <summary>The playback link itself. Outside the signed-in group: the link is the permission, for a few hours.</summary>
    public static void MapPlayback(this WebApplication app) => app.MapGet("/api/play/{token}", async (HttpContext h, PlaybackLinks links, MeetingService s, string token) =>
    {
        if (links.Resolve(token) is not { } id) return Results.NotFound();
        var media = await s.OpenLinkedAsync(id);
        h.Response.RegisterForDisposeAsync(media);
        h.Response.Headers.CacheControl = "private, no-store";
        return Results.Stream(media.Content, media.ContentType, enableRangeProcessing: true);
    });
}

/// <summary>Checks on Meet's note-takers every minute and brings back finished meetings.</summary>
public sealed class MeetWorker(MeetService meet, FailureReporter reporter) : BackgroundService
{
    public static readonly TimeSpan Every = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(Every);
        do
        {
            try { await meet.CheckAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { await reporter.ReportAsync("note-taker", null, ex); }
        } while (await timer.WaitForNextTickAsync(ct));
    }
}

/// <summary>Reads connected calendars every five minutes and books (or calls off) note-takers for their online meetings.</summary>
public sealed class CalendarWorker(CalendarService calendars, FailureReporter reporter) : BackgroundService
{
    public static readonly TimeSpan Every = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(Every);
        do
        {
            try { await calendars.SyncAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { await reporter.ReportAsync("calendar", null, ex); }
        } while (await timer.WaitForNextTickAsync(ct));
    }
}
