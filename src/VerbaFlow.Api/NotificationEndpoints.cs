using VerbaFlow.Infrastructure.Services;

namespace VerbaFlow.Api;

public sealed record NotificationReadBody(Guid? Id);
public sealed record NotificationSettingsBody(IReadOnlyList<string>? EmailOn);

/// <summary>Each person's own notices (the bell) and their choice of what comes by email.</summary>
public static class NotificationEndpoints
{
    public static void MapNotificationEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/notifications", (HttpContext h, NotificationService n) => n.ListAsync(h.Current()));
        api.MapPost("/notifications/read", (HttpContext h, NotificationService n, NotificationReadBody b) => n.MarkReadAsync(h.Current(), b.Id));
        api.MapGet("/notifications/settings", (HttpContext h, NotificationService n) => n.SettingsAsync(h.Current()));
        api.MapPut("/notifications/settings", (HttpContext h, NotificationService n, NotificationSettingsBody b) => n.SetEmailAsync(h.Current(), b.EmailOn ?? []));
    }
}

/// <summary>Turns new history entries into notices every 15 seconds, and looks for due dates every 10 minutes.</summary>
public sealed class NotificationWorker(NotificationService notifications, FailureReporter reporter) : BackgroundService
{
    public static readonly TimeSpan Every = TimeSpan.FromSeconds(15);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(Every);
        do
        {
            try { await notifications.ProcessAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { await reporter.ReportAsync("notifications", null, ex); }
        } while (await timer.WaitForNextTickAsync(ct));
    }
}
