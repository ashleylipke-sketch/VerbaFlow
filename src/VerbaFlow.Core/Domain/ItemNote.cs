namespace VerbaFlow.Core.Domain;

/// <summary>
/// A short note left on a recording for the people working on it ("check the client's name", "the second page is
/// urgent"). It belongs to the whole recording (all its versions), like attached files. A note can be ticked as dealt
/// with; only its writer can take it away, and approval locks the notes with the rest.
/// </summary>
public sealed record ItemNote(Guid Id, Guid ChainId, Guid AddedOnItemId, Guid AuthorId, string Text, DateTimeOffset At,
    Guid? DoneBy = null, DateTimeOffset? DoneAt = null)
{
    public const int MaxLength = 2000;
    public const int MaxPerItem = 200;
    public bool Done => DoneAt is not null;
}
