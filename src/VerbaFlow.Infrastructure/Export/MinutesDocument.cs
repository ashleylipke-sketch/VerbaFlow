namespace VerbaFlow.Infrastructure.Export;

/// <summary>
/// Everything that goes into an exported meeting, already worked out and in reading order. The Word and PDF writers
/// only lay this out; they decide nothing. All text here is plain text: transcript and AI text are never read as markup.
/// </summary>
/// <param name="Marker">"Approved", "Draft" or "Superseded".</param>
/// <param name="MarkerLine">The line under the marker, for example "Approved by Carol Admin on 11 October 2026 17:22".</param>
/// <param name="MinutesLines">The minutes as lines: a heading ends with a colon, a bullet starts with "- ".</param>
/// <param name="Footer">Printed at the foot of every page.</param>
public sealed record MinutesDocument(
    string Title,
    string Marker,
    string MarkerLine,
    IReadOnlyList<(string Label, string Value)> Details,
    string? Summary,
    IReadOnlyList<string> ActionPoints,
    IReadOnlyList<string> MinutesLines,
    string? Tone,
    IReadOnlyList<MinutesPassage> Transcript,
    string Footer);

public sealed record MinutesPassage(string Time, string Speaker, string Text);

/// <summary>The parts a person can choose to include. The title, marker and details are always included.</summary>
public static class ExportParts
{
    public const string Summary = "summary", Minutes = "minutes", Actions = "actions", Transcript = "transcript", Tone = "tone";
    public static readonly IReadOnlyList<string> All = [Summary, Minutes, Actions, Transcript, Tone];

    private static readonly Dictionary<string, string> Names = new()
    {
        [Summary] = "Summary", [Minutes] = "Minutes", [Actions] = "Action points", [Transcript] = "Transcript", [Tone] = "Tone of the meeting",
    };

    public static string NameOf(string part) => Names[part];

    /// <summary>A comma-separated list such as "summary,transcript". Nothing given means everything.</summary>
    public static IReadOnlyList<string> Parse(string? text)
    {
        if (text is null) return All;
        var chosen = text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => p.ToLowerInvariant() switch { "action" or "actionpoints" or "action-points" => Actions, var x => x }).ToHashSet();
        var unknown = chosen.FirstOrDefault(c => !Names.ContainsKey(c));
        if (unknown is not null) throw new VerbaFlow.Core.Domain.DomainException("Choose from Summary, Minutes, Action points, Transcript and Tone.");
        if (chosen.Count == 0) throw new VerbaFlow.Core.Domain.DomainException("Choose at least one part to include.");
        return All.Where(chosen.Contains).ToList(); // always in reading order
    }
}

public static class ExportFormats
{
    public const string Word = "docx";
    public const string Pdf = "pdf";

    public static string ContentType(string format) => format == Word
        ? "application/vnd.openxmlformats-officedocument.wordprocessingml.document"
        : "application/pdf";
}
