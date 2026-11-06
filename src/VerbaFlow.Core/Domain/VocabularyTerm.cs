namespace VerbaFlow.Core.Domain;

/// <summary>A name or term the speech service should expect: a person, client, product or piece of jargon.</summary>
/// <param name="OwnerId">Null for the organisation's vocabulary; a person's id for the own list of someone with a personal account.</param>
public sealed record VocabularyTerm(Guid Id, string Text, string? Note, Guid AddedBy, DateTimeOffset AddedAt, Guid? OwnerId = null);
