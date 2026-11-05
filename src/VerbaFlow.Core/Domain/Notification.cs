namespace VerbaFlow.Core.Domain;

/// <summary>
/// One notice for one person: something happened that they should know about or act on (assigned to them, returned,
/// approved, ready, failed, due, or waiting for an administrator's agreement).
/// </summary>
/// <param name="Group">Which kind it is, for choosing what comes by email (see <see cref="NotificationGroups"/>).</param>
/// <param name="Link">Where it opens in VerbaFlow, as a page address such as "#/items/{id}".</param>
/// <param name="Email">What happened to the email copy: "sent", "not wanted", "no address", or "failed (VF-…)".</param>
public sealed record Notification(Guid Id, Guid UserId, string Group, string Text, Guid? ItemId, string Link,
    DateTimeOffset At, DateTimeOffset? ReadAt = null, string? Email = null);

/// <summary>A person's choice of which notices also come by email. Every notice is always shown in VerbaFlow.</summary>
/// <param name="Id">The person's user id.</param>
public sealed record NotificationSettings(Guid Id, IReadOnlyList<string> EmailOff);

/// <summary>How far the notifier has read the history, and when it last looked for due dates.</summary>
public sealed record NotificationState(Guid Id, long LastSeq, DateTimeOffset? DueCheckedAt = null);

public static class NotificationGroups
{
    public const string Work = "work", Ready = "ready", Due = "due", Approvals = "approvals";

    /// <summary>The groups in the order shown, with what each covers. Approvals only reach administrators.</summary>
    public static readonly IReadOnlyList<(string Key, string Label, bool AdminsOnly)> All =
    [
        (Work, "Assigned to you, returned to you, approved, reopened or deleted", false),
        (Ready, "A recording is ready, could not be converted, or a note-taker could not record", false),
        (Due, "Due tomorrow, due today or overdue", false),
        (Approvals, "A reopen, delete or retention change needs an administrator to agree", true),
    ];
}
