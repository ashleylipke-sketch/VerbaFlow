namespace VerbaFlow.Core.Domain;

/// <summary>
/// A comment on some words of a transcript or dictation, like a margin comment in Word: "is this the right client?",
/// "check this figure". It belongs to the whole recording (all its versions), like notes. It points at the words it was
/// made on: the passage, where in it, and the words themselves, so it can be found again after the text around it is
/// edited. People reply under it and tick it as resolved; approval locks comments with the rest.
/// </summary>
/// <param name="SegmentId">The passage the words were in when the comment was made (an edit gives a passage a new id).</param>
/// <param name="SegmentStartMs">Where that passage starts in the recording, which stays the same after edits.</param>
/// <param name="From">The first word's position in the passage (words counted on spaces).</param>
/// <param name="Quote">The words commented on, as they were.</param>
/// <param name="AtMs">Where the first of those words is spoken, to play from there.</param>
public sealed record TextComment(Guid Id, Guid ChainId, Guid AddedOnItemId, Guid SegmentId, int SegmentStartMs, int From, string Quote,
    int AtMs, Guid AuthorId, string Text, DateTimeOffset At, IReadOnlyList<CommentReply> Replies,
    Guid? ResolvedBy = null, DateTimeOffset? ResolvedAt = null)
{
    public const int MaxLength = 2000;
    public const int MaxWords = 200;
    public const int MaxPerItem = 300;
    public const int MaxReplies = 50;
    public bool Resolved => ResolvedAt is not null;
}

public sealed record CommentReply(Guid Id, Guid AuthorId, string Text, DateTimeOffset At);
