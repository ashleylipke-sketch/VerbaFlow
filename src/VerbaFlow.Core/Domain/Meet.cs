namespace VerbaFlow.Core.Domain;

/// <summary>Where a note-taker is in its work. Ends at Fetched (the item exists), Failed or Cancelled.</summary>
public enum NoteTakerState { Scheduled, Joining, WaitingToBeLetIn, Recording, MeetingEnded, Fetched, Failed, Cancelled }

/// <summary>
/// Meet: a note-taker that joins an online meeting (Zoom, Google Meet, Microsoft Teams, Webex) from its link, records
/// it as a visible participant, and brings the video back as a Meeting item. Nobody has to press record in the meeting,
/// and it works for meetings set up by other organisations (their host may have to let it in from the lobby).
/// </summary>
/// <param name="Requesters">Everyone in the organisation who asked for this meeting to be recorded, first asker first.
/// Only one note-taker joins a meeting however many colleagues ask; the first asker owns the item, the others can read it.</param>
/// <param name="MeetingKey">What identifies the meeting (see MeetingPlatforms.Key), used to spot a second request for the same meeting.</param>
/// <param name="BotId">The note-taker service's own id for this note-taker; null until it was booked.</param>
/// <param name="FailureReason">A plain, customer-safe sentence (with a VF- reference when something broke).</param>
public sealed record NoteTaker(Guid Id, IReadOnlyList<Guid> Requesters, string Name, string MeetingUrl, string MeetingKey, string Platform,
    DateTimeOffset JoinAt, string SpokenLanguages, DateTimeOffset CreatedAt, NoteTakerState State = NoteTakerState.Scheduled,
    string? BotId = null, DateTimeOffset? StateAt = null, Guid? ItemId = null, string? FailureReason = null)
{
    [System.Text.Json.Serialization.JsonIgnore] public Guid OwnerId => Requesters[0];
    [System.Text.Json.Serialization.JsonIgnore] public bool IsFinished => State is NoteTakerState.Fetched or NoteTakerState.Failed or NoteTakerState.Cancelled;
    /// <summary>Still to join or in the meeting: a new request for the same meeting joins this one instead of sending another.</summary>
    [System.Text.Json.Serialization.JsonIgnore] public bool IsActive => State is NoteTakerState.Scheduled or NoteTakerState.Joining or NoteTakerState.WaitingToBeLetIn or NoteTakerState.Recording;
}

/// <summary>One stretch of one participant speaking, as the meeting platform reported it, in ms from the start of the recording.</summary>
public sealed record PlatformSpeech(int StartMs, int EndMs, int Participant, string Name);

/// <summary>
/// Who the meeting platform said was speaking when, kept with the item (by its MediaSourceItemId) so the transcript's
/// speakers get people's names. Deleted with the item.
/// </summary>
public sealed record MeetingSpeakers(Guid ItemId, IReadOnlyList<PlatformSpeech> Stretches);

/// <summary>The meeting platforms the note-taker can join, worked out from the link.</summary>
public static class MeetingPlatforms
{
    public const string Zoom = "Zoom", GoogleMeet = "Google Meet", Teams = "Microsoft Teams", Webex = "Webex";

    /// <summary>The platform of a meeting link, or null when it is not a link the note-taker can join.</summary>
    public static string? Of(string? link)
    {
        if (!Uri.TryCreate((link ?? "").Trim(), UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps) return null;
        var h = u.Host.ToLowerInvariant();
        bool Is(string d) => h == d || h.EndsWith("." + d, StringComparison.Ordinal);
        if (Is("zoom.us") || Is("zoom.com") || Is("zoomgov.com")) return Zoom;
        if (h == "meet.google.com") return GoogleMeet;
        if (Is("teams.microsoft.com") || Is("teams.live.com") || Is("teams.microsoft.us")) return Teams;
        if (Is("webex.com")) return Webex;
        return null;
    }

    /// <summary>
    /// What identifies the meeting behind a link, so two colleagues' links to the same meeting match even when they differ
    /// in a passcode, a tracking part or capitals: Zoom's meeting number, Google Meet's code, the Teams meeting path, the
    /// Webex address and meeting number. Null when the link is not one the note-taker can join.
    /// </summary>
    public static string? Key(string? link)
    {
        var platform = Of(link);
        if (platform is null) return null;
        var u = new Uri(link!.Trim());
        var path = Uri.UnescapeDataString(u.AbsolutePath).TrimEnd('/').ToLowerInvariant();
        switch (platform)
        {
            case Zoom:
                // /j/123..., /w/123..., /s/123..., /wc/join/123..., /wc/123/join
                var digits = path.Split('/').FirstOrDefault(p => p.Length >= 9 && p.All(char.IsAsciiDigit));
                return digits is null ? $"zoom:{u.Host.ToLowerInvariant()}{path}" : $"zoom:{digits}";
            case GoogleMeet:
                return $"meet:{path.Trim('/')}";
            case Teams:
                return $"teams:{path}";
            default:
                var query = System.Web.HttpUtility.ParseQueryString(u.Query);
                var mtid = query["MTID"] ?? query["mtid"];
                return $"webex:{u.Host.ToLowerInvariant()}{path}{(mtid is null ? "" : "?mtid=" + mtid.ToLowerInvariant())}";
        }
    }
}
