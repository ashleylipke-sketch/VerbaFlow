namespace VerbaFlow.Core.Domain;

/// <summary>
/// A phrase, sentence or paragraphs the company keeps ready to drop into a dictation by saying "insert" and its code,
/// e.g. "insert legal 1.1". Paragraphs in the text are separated by line breaks.
/// <see cref="OwnerId"/> is null for the organisation's saved text, or the person's id for someone with a personal account's own.
/// </summary>
public sealed record SavedText(Guid Id, string Code, string Text, Guid AddedBy, DateTimeOffset AddedAt,
    Guid? ChangedBy = null, DateTimeOffset? ChangedAt = null, Guid? OwnerId = null);
