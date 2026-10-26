using System.Net;
using System.Net.Sockets;
using Ical.Net;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using VerbaFlow.Core.Domain;
using VerbaFlow.Core.Providers;

namespace VerbaFlow.Infrastructure.Calendars;

/// <summary>
/// Reads a calendar from its private iCal (.ics) address, which Outlook.com, Gmail, Apple and most other calendars can
/// give out. Repeating meetings, moved and cancelled occurrences and time zones are worked out by Ical.Net (MIT).
/// The address is only fetched over https, and never from the computer's own network (so it cannot be used to reach
/// services that are not meant to be public).
/// </summary>
public sealed class LinkCalendarReader : ICalendarReader
{
    public string Source => CalendarSources.Link;
    public const int MaxBytes = 5 * 1024 * 1024;
    private readonly HttpClient http;

    /// <param name="allowPrivateAddresses">Only for tests that serve a calendar from this computer.</param>
    public LinkCalendarReader(bool allowPrivateAddresses = false)
    {
        var handler = new SocketsHttpHandler
        {
            UseProxy = false, // the address check below is about the calendar's own server
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectCallback = async (ctx, ct) =>
            {
                var addresses = await Dns.GetHostAddressesAsync(ctx.DnsEndPoint.Host, ct);
                var allowed = addresses.Where(a => allowPrivateAddresses || IsPublic(a)).ToArray();
                if (allowed.Length == 0) throw new HttpRequestException($"The calendar address points at a private network address ({ctx.DnsEndPoint.Host}).");
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try { await socket.ConnectAsync(allowed, ctx.DnsEndPoint.Port, ct); return new NetworkStream(socket, ownsSocket: true); }
                catch { socket.Dispose(); throw; }
            },
        };
        http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("VerbaFlow-Calendar/1.0");
    }

    /// <summary>
    /// The address as it will be fetched (webcal:// becomes https://), or null when it is not an https calendar address.
    /// </summary>
    public static string? Normalise(string? link)
    {
        var s = (link ?? "").Trim();
        if (s.StartsWith("webcal://", StringComparison.OrdinalIgnoreCase) || s.StartsWith("webcals://", StringComparison.OrdinalIgnoreCase))
            s = "https://" + s[(s.IndexOf("://", StringComparison.Ordinal) + 3)..];
        return Uri.TryCreate(s, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps && s.Length <= 2000 ? u.AbsoluteUri : null;
    }

    /// <summary>A safe form of the address to show: the site only, since the rest works like a password.</summary>
    public static string Masked(string link) => Uri.TryCreate(link, UriKind.Absolute, out var u) ? $"{u.Host}/…" : "…";

    public async Task<IReadOnlyList<CalendarMeeting>> ReadAsync(CalendarSettings settings, User user, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var link = Normalise(settings.Link) ?? throw new ProviderException(FaultKind.Settings, "The calendar address is not an https address.");
        string text;
        try
        {
            using var res = await http.GetAsync(link, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!res.IsSuccessStatusCode)
                throw new ProviderException(res.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.Gone
                    ? FaultKind.Settings : FaultKind.Unavailable, $"The calendar address answered {(int)res.StatusCode}.");
            if (res.RequestMessage?.RequestUri is { Scheme: not "https" }) throw new ProviderException(FaultKind.Settings, "The calendar address moved to a non-https address.");
            await using var body = await res.Content.ReadAsStreamAsync(ct);
            using var limited = new MemoryStream();
            var buffer = new byte[81920];
            int n;
            while ((n = await body.ReadAsync(buffer, ct)) > 0)
            {
                if (limited.Length + n > MaxBytes) throw new ProviderException(FaultKind.Other, $"The calendar is larger than {MaxBytes / 1024 / 1024} MB.");
                limited.Write(buffer, 0, n);
            }
            text = System.Text.Encoding.UTF8.GetString(limited.ToArray());
        }
        catch (HttpRequestException ex) { throw new ProviderException(FaultKind.Unavailable, "The calendar address could not be reached: " + ex.Message); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { throw new ProviderException(FaultKind.Unavailable, "The calendar address took too long to answer."); }
        return Parse(text, from, to, user.Email);
    }

    /// <summary>The meetings in an iCal calendar that start between the two times (and those already running at the first).</summary>
    /// <param name="email">The person's own address: a meeting where that attendee declined counts as declined.</param>
    public static IReadOnlyList<CalendarMeeting> Parse(string ics, DateTimeOffset from, DateTimeOffset to, string? email = null)
    {
        if (!ics.TrimStart().StartsWith("BEGIN:VCALENDAR", StringComparison.OrdinalIgnoreCase))
            throw new ProviderException(FaultKind.Settings, "The address did not return a calendar (no BEGIN:VCALENDAR).");
        Calendar calendar;
        try { calendar = Calendar.Load(ics) ?? throw new FormatException("empty"); }
        catch (Exception ex) when (ex is not ProviderException) { throw new ProviderException(FaultKind.Other, "The calendar could not be read: " + ex.Message); }

        var list = new List<CalendarMeeting>();
        // Occurrences are worked out from a day before, so a long meeting already running is still seen.
        var start = new CalDateTime(from.UtcDateTime.AddDays(-1), "UTC");
        foreach (var o in calendar.GetOccurrences<CalendarEvent>(start).TakeWhile(o => o.Period.StartTime.AsUtc < to.UtcDateTime).Take(2000))
        {
            if (o.Source is not CalendarEvent e) continue;
            if (e.IsAllDay || string.Equals(e.Status, "CANCELLED", StringComparison.OrdinalIgnoreCase)) continue;
            var s = new DateTimeOffset(o.Period.StartTime.AsUtc, TimeSpan.Zero);
            var end = o.Period.EffectiveEndTime is { } et ? new DateTimeOffset(et.AsUtc, TimeSpan.Zero) : s.AddHours(1);
            if (end <= from) continue;
            // A moved occurrence keeps the key of the time it was first given.
            var original = e.RecurrenceIdentifier?.StartTime?.AsUtc ?? (e.RecurrenceRule is not null || e.RecurrenceDates.GetAllDates().Any() ? o.Period.StartTime.AsUtc : (DateTime?)null);
            var key = $"ics:{e.Uid}" + (original is { } t ? $"|{t:yyyyMMddTHHmmss}" : "");
            var extra = string.Join(" ", e.Properties.Where(p => p.Name is "X-GOOGLE-CONFERENCE" or "X-MICROSOFT-SKYPETEAMSMEETINGURL" or "X-MICROSOFT-ONLINEMEETINGCONFLINK")
                .Select(p => p.Value?.ToString()));
            var link = MeetingLinks.Find(extra, e.Location, e.Url?.ToString(), e.Description);
            var declined = email is { Length: > 0 } && e.Attendees.Any(a =>
                string.Equals(a.Value?.ToString().Replace("mailto:", "", StringComparison.OrdinalIgnoreCase), email, StringComparison.OrdinalIgnoreCase)
                && string.Equals(a.ParticipationStatus, "DECLINED", StringComparison.OrdinalIgnoreCase));
            var subject = string.IsNullOrWhiteSpace(e.Summary) ? "Meeting" : e.Summary.Trim();
            list.Add(new CalendarMeeting(key, subject.Length > 200 ? subject[..200] : subject, s, end, link, declined));
        }
        return list;
    }

    /// <summary>Whether an address is on the public internet (not this computer, a private network, or link-local).</summary>
    public static bool IsPublic(IPAddress a)
    {
        if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
        if (IPAddress.IsLoopback(a) || a.Equals(IPAddress.Any) || a.Equals(IPAddress.IPv6Any) || a.Equals(IPAddress.IPv6None)) return false;
        if (a.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = a.GetAddressBytes();
            return !(b[0] == 10 || b[0] == 0 || b[0] == 127 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168)
                || (b[0] == 169 && b[1] == 254) || (b[0] == 100 && b[1] >= 64 && b[1] <= 127) || b[0] >= 224);
        }
        return !(a.IsIPv6LinkLocal || a.IsIPv6SiteLocal || a.IsIPv6Multicast || (a.GetAddressBytes()[0] & 0xFE) == 0xFC);
    }
}
