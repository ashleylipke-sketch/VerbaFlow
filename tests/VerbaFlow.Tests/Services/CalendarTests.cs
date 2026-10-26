using System.Net;
using System.Text.Json.Nodes;
using VerbaFlow.Core.Domain;
using VerbaFlow.Core.Providers;
using VerbaFlow.Infrastructure.Calendars;
using VerbaFlow.Infrastructure.Services;

namespace VerbaFlow.Tests.Services;

/// <summary>A calendar the test fills in, per person; can be made to fail.</summary>
public sealed class FakeCalendar(string source = CalendarSources.Sample) : ICalendarReader
{
    public string Source => source;
    public Dictionary<Guid, List<CalendarMeeting>> Meetings { get; } = [];
    public bool Fail;
    public int Reads;

    public Task<IReadOnlyList<CalendarMeeting>> ReadAsync(CalendarSettings settings, User user, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        Reads++;
        if (Fail) throw new ProviderException(FaultKind.Unavailable, "calendar down");
        IReadOnlyList<CalendarMeeting> list = (Meetings.GetValueOrDefault(user.Id) ?? []).Where(m => m.End > from && m.Start < to).ToList();
        return Task.FromResult(list);
    }

    public void Add(User u, CalendarMeeting m) { if (!Meetings.TryGetValue(u.Id, out var l)) Meetings[u.Id] = l = []; l.Add(m); }
}

public class CalendarTests
{
    private const string ZoomLink = "https://us02web.zoom.us/j/81234567890?pwd=abc";

    private static (CountingNoteTaker Bot, MeetService Meet, FakeCalendar Cal, CalendarService Calendars) Make(Env env)
    {
        var bot = new CountingNoteTaker(env.Clock);
        var meet = new MeetService(env.Stores, bot, env.Meeting, env.Clock, env.Reporter);
        var cal = new FakeCalendar();
        return (bot, meet, cal, new CalendarService(env.Stores, [cal], meet, env.Clock, env.Reporter));
    }

    private static CalendarMeeting Meeting(string key, double startMinutes, string? link = ZoomLink, bool declined = false, string subject = "Board") =>
        new(key, subject, T.Now.AddMinutes(startMinutes), T.Now.AddMinutes(startMinutes + 60), link, declined);

    private static Task Connect(CalendarService c, User u, bool auto = true) => c.ConnectAsync(u, new CalendarConnect(CalendarSources.Sample, null, auto, "en-GB"));

    [Theory]
    [InlineData("Join: <https://us02web.zoom.us/j/81234567890?pwd=abc>.", "https://us02web.zoom.us/j/81234567890?pwd=abc")]
    [InlineData("https://eur01.safelinks.protection.outlook.com/?url=https%3A%2F%2Fmeet.google.com%2Fabc-defg-hij&data=x", "https://meet.google.com/abc-defg-hij")]
    [InlineData("See https://www.google.com/url?q=https://acme.webex.com/meet/pat&sa=D first", "https://acme.webex.com/meet/pat")]
    [InlineData("Agenda https://example.com/doc then https://teams.microsoft.com/l/meetup-join/19%3ameeting_X%40thread.v2/0?context=%7b%7d", "https://teams.microsoft.com/l/meetup-join/19%3ameeting_X%40thread.v2/0?context=%7b%7d")]
    [InlineData("&lt;https://zoom.us/j/81234567890&gt;", "https://zoom.us/j/81234567890")]
    public void The_meeting_link_is_found_in_the_invitation(string text, string link) => Assert.Equal(link, MeetingLinks.Find(null, text));

    [Theory]
    [InlineData("http://zoom.us/j/81234567890")]
    [InlineData("Room 4, second floor")]
    [InlineData("https://zoom.us.evil.example/j/81234567890")]
    public void No_link_is_found_where_there_is_none_the_note_taker_can_join(string text) => Assert.Null(MeetingLinks.Find(text));

    [Fact]
    public void Microsoft_365_events_are_read_with_the_teams_link_and_a_key_that_survives_a_move()
    {
        var occurrence = JsonNode.Parse("""
            {"id":"AAMk-occ","subject":"Weekly","seriesMasterId":"AAMk-master","originalStart":"2026-10-08T09:00:00Z",
             "start":{"dateTime":"2026-10-08T10:00:00.0000000","timeZone":"UTC"},"end":{"dateTime":"2026-10-08T10:30:00.0000000","timeZone":"UTC"},
             "isCancelled":false,"isAllDay":false,"location":{"displayName":"Microsoft Teams Meeting"},
             "onlineMeeting":{"joinUrl":"https://teams.microsoft.com/l/meetup-join/19%3ameeting_X%40thread.v2/0"},
             "responseStatus":{"response":"declined"},"body":{"content":"Hi"}}
            """)!;
        var m = GraphCalendarReader.Read(occurrence)!;
        Assert.Equal("ms:AAMk-master|20261008T090000", m.EventKey);
        Assert.Equal(new DateTimeOffset(2026, 10, 8, 10, 0, 0, TimeSpan.Zero), m.Start);
        Assert.Equal("Microsoft Teams", MeetingPlatforms.Of(m.JoinUrl));
        Assert.True(m.Declined);

        var zoomInBody = JsonNode.Parse("""
            {"id":"AAMk-one","subject":"","start":{"dateTime":"2026-10-08T10:00:00"},"end":{"dateTime":"2026-10-08T11:00:00"},
             "location":{"displayName":""},"body":{"content":"Join Zoom Meeting https://zoom.us/j/81234567890?pwd=x"}}
            """)!;
        var z = GraphCalendarReader.Read(zoomInBody)!;
        Assert.Equal(("ms:AAMk-one", "Meeting", "Zoom"), (z.EventKey, z.Subject, MeetingPlatforms.Of(z.JoinUrl)));

        Assert.Null(GraphCalendarReader.Read(JsonNode.Parse("""{"id":"c","isCancelled":true,"start":{"dateTime":"2026-10-08T10:00:00"},"end":{"dateTime":"2026-10-08T11:00:00"}}""")!));
        Assert.Null(GraphCalendarReader.Read(JsonNode.Parse("""{"id":"d","isAllDay":true,"start":{"dateTime":"2026-10-08T00:00:00"},"end":{"dateTime":"2026-10-09T00:00:00"}}""")!));
    }

    private const string Ics = """
        BEGIN:VCALENDAR
        VERSION:2.0
        PRODID:test
        BEGIN:VTIMEZONE
        TZID:Europe/London
        BEGIN:STANDARD
        DTSTART:19701025T020000
        TZOFFSETFROM:+0100
        TZOFFSETTO:+0000
        RRULE:FREQ=YEARLY;BYDAY=-1SU;BYMONTH=10
        END:STANDARD
        BEGIN:DAYLIGHT
        DTSTART:19700329T010000
        TZOFFSETFROM:+0000
        TZOFFSETTO:+0100
        RRULE:FREQ=YEARLY;BYDAY=-1SU;BYMONTH=3
        END:DAYLIGHT
        END:VTIMEZONE
        BEGIN:VEVENT
        UID:weekly-1
        SUMMARY:Weekly team call
        DTSTART;TZID=Europe/London:20261001T140000
        DTEND;TZID=Europe/London:20261001T143000
        RRULE:FREQ=WEEKLY;COUNT=6
        EXDATE;TZID=Europe/London:20261015T140000
        LOCATION:https://zoom.us/j/81234567890
        END:VEVENT
        BEGIN:VEVENT
        UID:weekly-1
        RECURRENCE-ID;TZID=Europe/London:20261022T140000
        SUMMARY:Weekly team call (moved)
        DTSTART;TZID=Europe/London:20261022T160000
        DTEND;TZID=Europe/London:20261022T163000
        LOCATION:https://zoom.us/j/81234567890
        END:VEVENT
        BEGIN:VEVENT
        UID:gmeet-2
        SUMMARY:Supplier review
        DTSTART:20261009T090000Z
        DTEND:20261009T093000Z
        X-GOOGLE-CONFERENCE:https://meet.google.com/abc-defg-hij
        DESCRIPTION:Join with Google Meet
        ATTENDEE;PARTSTAT=DECLINED:mailto:alice@example.test
        END:VEVENT
        BEGIN:VEVENT
        UID:cancelled-3
        SUMMARY:Cancelled
        STATUS:CANCELLED
        DTSTART:20261009T120000Z
        DTEND:20261009T130000Z
        LOCATION:https://zoom.us/j/99999999999
        END:VEVENT
        END:VCALENDAR
        """;

    [Fact]
    public void A_calendar_address_is_read_with_repeats_moves_time_zones_and_declines()
    {
        var list = LinkCalendarReader.Parse(Ics, T.Now, T.Now.AddDays(28), "alice@example.test");
        // 8 Oct (London summer time), 15 Oct left out, 22 Oct moved to 16:00, 29 Oct after the clocks change.
        var weekly = list.Where(m => m.EventKey.StartsWith("ics:weekly-1")).OrderBy(m => m.Start).ToList();
        Assert.Equal([new DateTimeOffset(2026, 10, 8, 13, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 10, 22, 15, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 10, 29, 14, 0, 0, TimeSpan.Zero)], weekly.Select(m => m.Start));
        Assert.Equal("ics:weekly-1|20261022T130000", weekly[1].EventKey); // keeps the key of the time it was first given
        Assert.Equal("Weekly team call (moved)", weekly[1].Subject);
        var g = Assert.Single(list, m => m.EventKey == "ics:gmeet-2");
        Assert.Equal(("https://meet.google.com/abc-defg-hij", true), (g.JoinUrl, g.Declined));
        Assert.DoesNotContain(list, m => m.EventKey.Contains("cancelled"));
        Assert.Throws<ProviderException>(() => LinkCalendarReader.Parse("<html>sign in</html>", T.Now, T.Now.AddDays(1)));
    }

    [Theory]
    [InlineData("webcal://p01-calendars.icloud.com/published/2/abc", "https://p01-calendars.icloud.com/published/2/abc")]
    [InlineData(" https://outlook.office365.com/owa/calendar/x/y/calendar.ics ", "https://outlook.office365.com/owa/calendar/x/y/calendar.ics")]
    [InlineData("http://example.com/cal.ics", null)]
    [InlineData("file:///etc/passwd", null)]
    public void Calendar_addresses_must_be_https(string link, string? expected) => Assert.Equal(expected, LinkCalendarReader.Normalise(link));

    [Theory]
    [InlineData("127.0.0.1", false)]
    [InlineData("10.1.2.3", false)]
    [InlineData("172.20.0.1", false)]
    [InlineData("192.168.1.10", false)]
    [InlineData("169.254.169.254", false)] // cloud metadata
    [InlineData("::1", false)]
    [InlineData("fd00::1", false)]
    [InlineData("::ffff:10.0.0.1", false)]
    [InlineData("52.96.0.1", true)]
    [InlineData("2603:1026::1", true)]
    public void Calendar_addresses_on_private_networks_are_never_fetched(string ip, bool isPublic) =>
        Assert.Equal(isPublic, LinkCalendarReader.IsPublic(IPAddress.Parse(ip)));

    [Fact]
    public async Task A_calendar_address_on_this_computer_is_refused_when_connecting()
    {
        using var env = new Env();
        var (_, meet, _, _) = Make(env);
        var calendars = new CalendarService(env.Stores, [new LinkCalendarReader()], meet, env.Clock, env.Reporter);
        var ex = await Assert.ThrowsAsync<DomainException>(() => calendars.ConnectAsync(env.Alice, new CalendarConnect(CalendarSources.Link, "https://127.0.0.1:9/cal.ics", true, "en-GB")));
        Assert.Contains("could not be read from that address", ex.Message);
        Assert.Null(await env.Stores.CalendarSettings.GetAsync(env.Alice.Id));
        Assert.Equal("…", LinkCalendarReader.Masked("not a link"));
        Assert.Equal("calendar.google.com/…", LinkCalendarReader.Masked("https://calendar.google.com/calendar/ical/x/private-secret/basic.ics"));
    }

    [Fact]
    public async Task Meetings_in_colleagues_calendars_get_one_note_taker_an_hour_ahead_and_switching_off_only_takes_that_person_off()
    {
        using var env = new Env();
        var (bot, meet, cal, calendars) = Make(env);
        cal.Add(env.Alice, Meeting("a-board", 90));
        cal.Add(env.Bob, Meeting("b-board", 90, "https://zoom.us/j/81234567890?pwd=other")); // the same meeting in Bob's calendar
        cal.Add(env.Alice, Meeting("a-room", 90, link: null, subject: "In the office"));
        await Connect(calendars, env.Alice);
        await Connect(calendars, env.Bob);
        Assert.Equal(0, bot.Scheduled); // more than an hour ahead

        var o = await calendars.OverviewAsync(env.Alice);
        var row = Assert.Single(o.Meetings);
        Assert.Equal(("Board", "Zoom", true, (bool?)null, (Guid?)null), (row.Subject, row.Platform, row.Send, row.Choice, row.NoteTakerId));
        Assert.Equal(1, o.OtherMeetings);

        env.Clock.Now = T.Now.AddMinutes(35);
        await calendars.SyncAsync();
        Assert.Equal(1, bot.Scheduled);
        var nt = Assert.Single(await env.Stores.NoteTakers.ListAsync());
        Assert.Equal([env.Alice.Id, env.Bob.Id], nt.Requesters);
        Assert.Equal(T.Now.AddMinutes(90), nt.JoinAt);
        Assert.Equal("Booked", (await calendars.OverviewAsync(env.Alice)).Meetings[0].NoteTakerLabel);
        await calendars.SyncAsync(); // nothing new on the next read
        Assert.Equal(1, bot.Scheduled);

        // Alice switches it off: she comes off, Bob keeps the note-taker.
        await calendars.SwitchAsync(env.Alice, new CalendarSwitch("a-board", false));
        nt = (await env.Stores.NoteTakers.GetAsync(nt.Id))!;
        Assert.Equal([env.Bob.Id], nt.Requesters);
        Assert.Equal(0, bot.Cancelled);
        var aliceRow = (await calendars.OverviewAsync(env.Alice)).Meetings[0];
        Assert.Equal((false, (bool?)false, (Guid?)null), (aliceRow.Send, aliceRow.Choice, aliceRow.NoteTakerId));
        await calendars.SyncAsync();
        Assert.Equal([env.Bob.Id], (await env.Stores.NoteTakers.GetAsync(nt.Id))!.Requesters); // her choice holds

        // The meeting is cancelled in Bob's calendar: nobody needs the note-taker any more.
        cal.Meetings[env.Bob.Id].Clear();
        await calendars.SyncAsync();
        Assert.Equal(NoteTakerState.Cancelled, (await env.Stores.NoteTakers.GetAsync(nt.Id))!.State);
        Assert.Equal(1, bot.Cancelled);

        var audit = (await env.Stores.Audit.ListAsync()).Where(a => a.Product == "meet").ToList();
        Assert.Contains(audit, a => a.Type == "meet.booked" && a.Details.Contains("calendar"));
        Assert.Contains(audit, a => a.Type == "calendar.meeting_switched");
        Assert.DoesNotContain("Board", string.Concat(audit.Select(a => a.Details))); // meeting subjects stay out of History
        Assert.DoesNotContain("pwd", string.Concat(audit.Select(a => a.Details)));
    }

    [Fact]
    public async Task With_automatic_booking_off_only_meetings_switched_on_get_the_note_taker_and_declined_ones_never()
    {
        using var env = new Env();
        var (bot, _, cal, calendars) = Make(env);
        cal.Add(env.Alice, Meeting("one", 30));
        cal.Add(env.Alice, Meeting("two", 40, "https://meet.google.com/abc-defg-hij"));
        cal.Add(env.Alice, Meeting("declined", 20, "https://acme.webex.com/meet/pat", declined: true));
        await Connect(calendars, env.Alice, auto: false);
        Assert.Equal(0, bot.Scheduled);

        var o = await calendars.SwitchAsync(env.Alice, new CalendarSwitch("two", true));
        Assert.Equal(1, bot.Scheduled);
        Assert.Equal("Google Meet", MeetingPlatforms.Of(bot.Requests[0].MeetingUrl));
        Assert.Equal([false, false, true], o.Meetings.Select(m => m.Send));

        o = await calendars.SwitchAsync(env.Alice, new CalendarSwitch("declined", true));
        Assert.Equal(1, bot.Scheduled);
        Assert.Equal("You declined this meeting.", o.Meetings.Single(m => m.EventKey == "declined").Why);
        await Assert.ThrowsAsync<NotFoundException>(() => calendars.SwitchAsync(env.Alice, new CalendarSwitch("nope", true)));
    }

    [Fact]
    public async Task A_moved_meeting_has_its_note_taker_called_off_and_booked_again_for_the_new_time()
    {
        using var env = new Env();
        var (bot, _, cal, calendars) = Make(env);
        cal.Add(env.Alice, Meeting("m", 30));
        await Connect(calendars, env.Alice);
        Assert.Equal(1, bot.Scheduled);
        var first = Assert.Single(await env.Stores.NoteTakers.ListAsync());

        cal.Meetings[env.Alice.Id][0] = Meeting("m", 180);
        await calendars.SyncAsync();
        Assert.Equal(NoteTakerState.Cancelled, (await env.Stores.NoteTakers.GetAsync(first.Id))!.State);
        Assert.Equal(1, bot.Scheduled);

        env.Clock.Now = T.Now.AddMinutes(130);
        await calendars.SyncAsync();
        Assert.Equal(2, bot.Scheduled);
        Assert.Equal(T.Now.AddMinutes(180), (await env.Stores.NoteTakers.ListAsync()).Single(n => n.IsActive).JoinAt);
    }

    [Fact]
    public async Task A_note_taker_booked_by_hand_is_never_called_off_by_the_calendar()
    {
        using var env = new Env();
        var (bot, meet, cal, calendars) = Make(env);
        await meet.BookAsync(env.Alice, new NoteTakerBooking(ZoomLink, "Board", T.Now.AddMinutes(30), "en-GB"));
        cal.Add(env.Alice, Meeting("m", 30));
        await Connect(calendars, env.Alice);
        Assert.Equal(1, bot.Scheduled);
        await calendars.SwitchAsync(env.Alice, new CalendarSwitch("m", false));
        cal.Meetings[env.Alice.Id].Clear();
        await calendars.SyncAsync();
        await calendars.DisconnectAsync(env.Alice);
        Assert.Equal(0, bot.Cancelled);
        Assert.True(Assert.Single(await env.Stores.NoteTakers.ListAsync()).IsActive);
    }

    [Fact]
    public async Task A_calendar_that_cannot_be_read_is_reported_once_and_nothing_is_called_off()
    {
        using var env = new Env();
        var (bot, _, cal, calendars) = Make(env);
        cal.Add(env.Alice, Meeting("m", 30));
        await Connect(calendars, env.Alice);
        Assert.Equal(1, bot.Scheduled);
        cal.Fail = true;
        await calendars.SyncAsync();
        await calendars.SyncAsync();
        env.Clock.Now = T.Now.AddMinutes(5);
        var o = await calendars.OverviewAsync(env.Alice);
        Assert.StartsWith("Your calendar could not be read from that address just now.", o.Connected!.Problem);
        Assert.Contains("Reference: VF-", o.Connected.Problem);
        Assert.Single(await env.Stores.SupportErrors.ListAsync());
        Assert.Equal(0, bot.Cancelled);
        Assert.Single(o.Meetings); // the meetings read last time are still shown

        cal.Fail = false;
        await calendars.SyncAsync();
        Assert.Null((await calendars.OverviewAsync(env.Alice)).Connected!.Problem);
    }

    [Fact]
    public async Task Disconnecting_takes_the_person_off_what_the_calendar_booked()
    {
        using var env = new Env();
        var (bot, _, cal, calendars) = Make(env);
        cal.Add(env.Alice, Meeting("m", 30));
        await Connect(calendars, env.Alice);
        Assert.Equal(1, bot.Scheduled);
        var o = await calendars.DisconnectAsync(env.Alice);
        Assert.Null(o.Connected);
        Assert.Equal(1, bot.Cancelled);
        Assert.Empty(await env.Stores.CalendarChoices.ListAsync());
        Assert.Equal([CalendarSources.Sample], o.Sources.Select(s => s.Id)); // no Microsoft sign-in, no Microsoft 365 reader here
        await Assert.ThrowsAsync<DomainException>(() => calendars.ConnectAsync(env.Alice, new CalendarConnect(CalendarSources.Microsoft, null, true, "en-GB")));
    }
}
