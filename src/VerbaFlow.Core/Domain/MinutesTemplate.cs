namespace VerbaFlow.Core.Domain;

/// <summary>One part of exported minutes in a template: which part, the heading printed over it, and whether it is ticked
/// by default in the Export menu. A template lists every part once, in the order they are printed.</summary>
/// <param name="Part">"summary", "actions", "minutes", "tone" or "transcript".</param>
public sealed record TemplateSection(string Part, string Heading, bool On);

/// <summary>
/// How exported meeting minutes look, set by admins: the title line, a logo, the font and heading colour, which parts go in
/// and in what order (with their headings), whether the transcript shows times, a footer text, and whether the minutes are
/// laid out on the organisation's letterhead. The title, Approved/Draft marker and details table are always printed.
/// One template can be the default; without any, <see cref="Standard"/> is used (VerbaFlow's own layout).
/// </summary>
/// <param name="Title">The title line; {name} is the meeting's name, {date} the day it was recorded.</param>
/// <param name="Colour">The colour of the title and headings, as #RRGGBB.</param>
/// <param name="LogoPlace">"left" or "right" at the top of the first page.</param>
/// <param name="FooterText">Printed first in the footer, before the meeting's name, marker and reference (which always stay).</param>
public sealed record MinutesTemplate(Guid Id, string Name, bool IsDefault, string Title, string Font, string Colour,
    string? LogoSha256, string? LogoType, string LogoPlace, bool OnLetterhead, IReadOnlyList<TemplateSection> Sections,
    bool TranscriptTimes, string FooterText, Guid CreatedBy, DateTimeOffset CreatedAt, Guid? ChangedBy = null, DateTimeOffset? ChangedAt = null)
{
    public static readonly IReadOnlyList<string> Fonts = ["Arial", "Calibri", "Times New Roman", "Georgia", "Verdana"];

    /// <summary>The parts and their usual headings, in VerbaFlow's own order.</summary>
    public static readonly IReadOnlyList<TemplateSection> StandardSections =
    [
        new("summary", "Summary", true), new("actions", "Action points", true), new("minutes", "Minutes", true),
        new("tone", "Tone of the meeting", true), new("transcript", "Transcript", true),
    ];

    /// <summary>VerbaFlow's own layout, used when no template is chosen or none has been made.</summary>
    public static MinutesTemplate Standard { get; } = new(Guid.Empty, "VerbaFlow standard", false, "{name}", "Arial", "#000000",
        null, null, "left", false, StandardSections, true, "VerbaFlow", Guid.Empty, DateTimeOffset.UnixEpoch);
}
