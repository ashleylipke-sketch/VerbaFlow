namespace VerbaFlow.Core.Domain;

/// <summary>
/// How long one kind of item is kept. Null means keep for ever.
/// </summary>
/// <param name="DeleteApprovedAfterDays">An approved item is deleted (audio, text and summaries; the tombstone and History stay) this many days after approval.</param>
/// <param name="RemoveRecordingAfterDays">An approved item's recording (sound or video) is removed this many days after approval; the text, minutes and History stay.</param>
/// <param name="DeleteUnapprovedAfterDays">An item that was never approved is deleted when nothing has happened to it for this many days.</param>
public sealed record RetentionRules(int? DeleteApprovedAfterDays = null, int? RemoveRecordingAfterDays = null, int? DeleteUnapprovedAfterDays = null)
{
    public const int MaxDays = 36500;
    public bool KeepsEverything => DeleteApprovedAfterDays is null && RemoveRecordingAfterDays is null && DeleteUnapprovedAfterDays is null;

    /// <summary>Refuses numbers that make no sense, with a plain sentence naming the kind of item.</summary>
    public void Check(string what)
    {
        foreach (var d in new[] { DeleteApprovedAfterDays, RemoveRecordingAfterDays, DeleteUnapprovedAfterDays })
            if (d is < 1 or > MaxDays) throw new DomainException($"{what}: the number of days must be between 1 and {MaxDays}, or left empty to keep them.");
        if (RemoveRecordingAfterDays is { } r && DeleteApprovedAfterDays is { } a && r >= a)
            throw new DomainException($"{what}: the recording would be deleted with the rest of the item anyway, so removing it on its own must come sooner, or be left empty.");
    }
}

/// <summary>
/// The organisation's retention rules (one record). Changing them needs two different administrators: one proposes,
/// another agrees. New rules only start deleting a week after they are agreed, so a mistake can be put right first.
/// </summary>
/// <param name="AppliesFrom">Nothing is deleted under the current rules before this time.</param>
/// <param name="Proposal">A change waiting for a second administrator; null when there is none.</param>
public sealed record RetentionPolicy(RetentionRules Meetings, RetentionRules Dictations, DateTimeOffset? AgreedAt = null,
    IReadOnlyList<Guid>? AgreedBy = null, DateTimeOffset? AppliesFrom = null, RetentionProposal? Proposal = null)
{
    public static readonly Guid Id = new("7e7e0000-0000-4000-8000-000000000001");
    /// <summary>New rules wait this long after they are agreed before anything is deleted under them.</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromDays(7);
    public static RetentionPolicy KeepEverything => new(new RetentionRules(), new RetentionRules());

    /// <summary>The rules for an item: dictations (Dictate and Speech) or meetings (including online meetings).</summary>
    public RetentionRules For(Item item) => item.IsDictation ? Dictations : Meetings;
}

public sealed record RetentionProposal(RetentionRules Meetings, RetentionRules Dictations, Guid ProposedBy, DateTimeOffset At, string? Note);

/// <summary>An administrator's hold on one recording (all its versions): the retention rules never delete it while held.</summary>
public sealed record RetentionHold(Guid ChainId, Guid By, DateTimeOffset At, string Reason);

/// <summary>Kept when the retention rules removed a recording's sound or video but kept its text (by the item whose media it was).</summary>
public sealed record RecordingRemoval(Guid SourceItemId, DateTimeOffset At, int AfterDays);
