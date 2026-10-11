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

public static class ExportFormats
{
    public const string Word = "docx";
    public const string Pdf = "pdf";

    public static string ContentType(string format) => format == Word
        ? "application/vnd.openxmlformats-officedocument.wordprocessingml.document"
        : "application/pdf";
}
