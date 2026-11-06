namespace VerbaFlow.Core.Domain;

/// <summary>One passage of a translation: the passage's id in the transcript version that was translated, and its text.</summary>
public sealed record TranslatedPassage(Guid SegmentId, string Text);

/// <summary>
/// A machine translation of one version of a recording (owner chose "Translate", 2026-11-06): its passages and, for a
/// meeting, its summary, action points, minutes and tone. The recording itself never changes; a translation is a copy to
/// read and export. It is made from one transcript version and one set of outputs, so a later edit makes it out of date.
/// </summary>
/// <param name="OutputsFingerprint">SHA-256 of the summary, actions, minutes and tone as translated (null when there were none).</param>
/// <param name="Untranslated">Passages the translator gave nothing back for; they are kept as spoken.</param>
public sealed record Translation(Guid Id, Guid ItemId, string Language, int TranscriptVersionNo, string? OutputsFingerprint,
    string Engine, Guid MadeBy, DateTimeOffset MadeAt, IReadOnlyList<TranslatedPassage> Passages,
    string? Summary, IReadOnlyList<string> ActionPoints, string? Minutes, string? Tone, int Untranslated = 0)
{
    /// <summary>The most languages one recording can be translated into.</summary>
    public const int MaxPerItem = 6;

    /// <summary>The author's sign-off closing as it was translated ("Yours sincerely," → "Cordialement,"), for letters and emails.</summary>
    public string? ClosingFrom { get; init; }
    public string? ClosingTo { get; init; }
}
