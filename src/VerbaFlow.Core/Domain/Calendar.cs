using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace VerbaFlow.Core.Domain;

/// <summary>Where a person's calendar is read from.</summary>
public static class CalendarSources
{
    /// <summary>Their Microsoft 365 (Outlook) calendar, read through Microsoft Graph with the organisation's permission.</summary>
    public const string Microsoft = "microsoft";
    /// <summary>A private calendar address (iCal / .ics) from Outlook.com, Gmail, Apple or any other calendar.</summary>
    public const string Link = "link";
    /// <summary>Stand-in: a made-up calendar with a few online meetings, so calendar booking can be tried without Microsoft 365.</summary>
    public const string Sample = "sample";

    public static string Label(string source) => source switch
    {
        Microsoft => "Microsoft 365 (Outlook) calendar",
        Link => "Calendar address (iCal link)",
        _ => "Sample calendar (stand-in)",
    };
}

/// <summary>
/// Meet: a person's calendar connection. Online meetings in it get the note-taker, automatically when AutoSend is on,
/// and the person can switch each meeting on or off.
/// </summary>
/// <param name="Link">For a calendar address: the private iCal address. It works like a password, so it is never shown back in full.</param>
/// <param name="Problem">A plain, customer-safe sentence (with a VF- reference) when the calendar could not be read last time.</param>
public sealed record CalendarSettings(Guid UserId, string Source, string? Link, bool AutoSend, string SpokenLanguages,
    DateTimeOffset ConnectedAt, DateTimeOffset? LastReadAt = null, string? Problem = null);

/// <summary>One meeting as read from a calendar, in UTC.</summary>
/// <param name="EventKey">Identifies the meeting (one occurrence of a repeating meeting) even when it is moved to another time.</param>
/// <param name="JoinUrl">The online-meeting link found in it, or null when it has none the note-taker can join.</param>
/// <param name="Declined">The person said they are not going.</param>
public sealed record CalendarMeeting(string EventKey, string Subject, DateTimeOffset Start, DateTimeOffset End, string? JoinUrl, bool Declined = false);

/// <summary>
/// What VerbaFlow knows about one meeting in one person's calendar: the person's own on/off choice (null = follow the
/// automatic setting) and the note-taker booked for it from the calendar.
/// </summary>
/// <param name="BookedHere">The calendar booked (or joined) the note-taker, so the calendar may also call it off. False when
/// the person had already booked it by hand: then the calendar never calls it off.</param>
/// <param name="BookedStart">The meeting's start when it was booked, to notice a meeting that was moved.</param>
public sealed record CalendarChoice(Guid Id, Guid UserId, string EventKey, bool? Send, DateTimeOffset Start, DateTimeOffset End,
    Guid? NoteTakerId = null, bool BookedHere = false, DateTimeOffset? BookedStart = null)
{
    /// <summary>The same id for the same person and meeting, every time.</summary>
    public static Guid IdFor(Guid userId, string eventKey) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes($"{userId:N}|{eventKey}")).AsSpan(0, 16));
}

/// <summary>Finds the online-meeting link in a calendar entry (its location, Teams link or invitation text).</summary>
public static partial class MeetingLinks
{
    [GeneratedRegex(@"https?://[^\s""'<>\]\[(){}|\\^`]+", RegexOptions.IgnoreCase)]
    private static partial Regex Url();

    /// <summary>The first link in the texts, in order, that the note-taker can join; null when there is none.</summary>
    public static string? Find(params string?[] texts)
    {
        foreach (var text in texts)
        {
            if (string.IsNullOrWhiteSpace(text)) continue;
            foreach (Match m in Url().Matches(WebUtility.HtmlDecode(text)))
            {
                var link = Unwrap(m.Value.TrimEnd('.', ',', ';', ':', '!', '?', '*', '>'));
                if (link is not null && MeetingPlatforms.Of(link) is not null) return link;
            }
        }
        return null;
    }

    /// <summary>Outlook's Safe Links and Google's redirect wrap the real link; this takes it out. Http links are refused.</summary>
    private static string? Unwrap(string link)
    {
        for (var i = 0; i < 3; i++)
        {
            if (!Uri.TryCreate(link, UriKind.Absolute, out var u)) return null;
            var host = u.Host.ToLowerInvariant();
            var q = System.Web.HttpUtility.ParseQueryString(u.Query);
            var inner = host.EndsWith("safelinks.protection.outlook.com", StringComparison.Ordinal) ? q["url"]
                : (host is "www.google.com" or "google.com") && u.AbsolutePath == "/url" ? q["q"] ?? q["url"]
                : null;
            if (inner is null) return u.Scheme == Uri.UriSchemeHttps ? link : null;
            link = inner;
        }
        return null;
    }
}
