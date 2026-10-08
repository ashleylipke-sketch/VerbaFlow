namespace VerbaFlow.Core.Domain;

/// <summary>A name or term the speech service should expect: a person, client, product or piece of jargon.</summary>
public sealed record VocabularyTerm(Guid Id, string Text, string? Note, Guid AddedBy, DateTimeOffset AddedAt);
