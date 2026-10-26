using VerbaFlow.Core.Domain;
using VerbaFlow.Core.Providers;

namespace VerbaFlow.Infrastructure.StandIns;

/// <summary>
/// Stand-in calendar: a few made-up meetings, timed from when the sample calendar was connected, so calendar booking can
/// be tried without Microsoft 365 or a calendar address. The first meeting starts about 20 minutes after connecting.
/// </summary>
public sealed class SampleCalendarReader : ICalendarReader
{
    public string Source => CalendarSources.Sample;

    public Task<IReadOnlyList<CalendarMeeting>> ReadAsync(CalendarSettings settings, User user, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var t = settings.ConnectedAt.UtcDateTime;
        var baseTime = new DateTimeOffset(t.Year, t.Month, t.Day, t.Hour, t.Minute / 5 * 5, 0, TimeSpan.Zero).AddMinutes(5);
        var id = settings.ConnectedAt.ToUnixTimeSeconds();
        CalendarMeeting M(string n, string subject, double startMinutes, int minutes, string? link, bool declined = false) =>
            new($"sample:{id}:{n}", subject, baseTime.AddMinutes(startMinutes), baseTime.AddMinutes(startMinutes + minutes), link, declined);
        var all = new[]
        {
            M("1", "Weekly team call (sample)", 20, 30, "https://zoom.us/j/5550100001?pwd=sample"),
            M("2", "Client catch-up (sample)", 120, 45, "https://teams.microsoft.com/l/meetup-join/19%3ameeting_sample%40thread.v2/0"),
            M("3", "Board meeting (sample, you declined)", 180, 60, "https://example.webex.com/meet/sample", declined: true),
            M("4", "Lunch (sample, not online)", 240, 60, null),
            M("5", "Supplier review (sample)", 24 * 60, 30, "https://meet.google.com/abc-defg-hij"),
        };
        IReadOnlyList<CalendarMeeting> list = all.Where(m => m.End > from && m.Start < to).ToList();
        return Task.FromResult(list);
    }
}
