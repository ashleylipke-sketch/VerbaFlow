using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace VerbaFlow.Infrastructure.Export;

/// <summary>Lays a <see cref="MinutesDocument"/> out as a Word file.</summary>
public static class DocxMinutesWriter
{
    /// <param name="letterhead">A Word letterhead to put the minutes on (from a minutes template), or null for a plain A4 page.</param>
    public static byte[] Write(MinutesDocument d, byte[]? letterhead = null)
    {
        var style = d.Style ?? MinutesStyle.Standard;
        if (letterhead is not null)
            return LetterWriter.IntoLetterhead(letterhead, main => Content(d, style, main).Select(DocxScripts.Mark).Append(Para(d.Footer, size: 16, color: "666666", font: style.Font)));
        using var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            var main = doc.AddMainDocumentPart();
            var body = new Body();
            main.Document = new Document(body);

            var footerPart = main.AddNewPart<FooterPart>();
            footerPart.Footer = new Footer(Para(d.Footer, size: 16, color: "666666", font: style.Font));
            var footerId = main.GetIdOfPart(footerPart);
            foreach (var e in Content(d, style, main)) body.Append(DocxScripts.Mark(e));

            body.Append(new SectionProperties(
                new FooterReference { Type = HeaderFooterValues.Default, Id = footerId },
                new PageSize { Width = 11906, Height = 16838 }, // A4
                new PageMargin { Top = 1134, Bottom = 1134, Left = 1134, Right = 1134, Header = 567, Footer = 567 }));
            main.Document.Save();
        }
        return ms.ToArray();
    }

    /// <summary>Everything in the body, in order: logo, title, marker, details, then the parts in the template's order.</summary>
    private static IEnumerable<OpenXmlElement> Content(MinutesDocument d, MinutesStyle style, MainDocumentPart main)
    {
        var f = style.Font;
        if (style.Logo is { } logo)
            yield return LetterWriter.PictureParagraph(main, logo, LetterWriter.Fit(logo, MinutesStyle.LogoHeightMm, MinutesStyle.LogoMaxWidthMm),
                "Logo", style.LogoPlace == "right");
        yield return Para(d.Title, bold: true, size: 36, color: style.Hex, font: f);
        yield return Para(d.W.Marker(d.Marker).ToUpper(d.W.Culture), bold: true, size: 24, color: d.Marker == "Approved" ? "1E7B34" : "B3261E", font: f);
        yield return Para(d.MarkerLine, size: 20, color: "444444", font: f);

        var table = new Table(new TableProperties(
            new TableWidth { Type = TableWidthUnitValues.Dxa, Width = "9638" },
            new TableBorders(new TopBorder { Val = BorderValues.Single, Size = 4, Color = "DDDDDD" },
                new BottomBorder { Val = BorderValues.Single, Size = 4, Color = "DDDDDD" },
                new InsideHorizontalBorder { Val = BorderValues.Single, Size = 4, Color = "DDDDDD" }),
            new TableLayout { Type = TableLayoutValues.Fixed }),
            new TableGrid(new GridColumn { Width = "2800" }, new GridColumn { Width = "6838" }));
        foreach (var (label, value) in d.Details)
            table.Append(new TableRow(
                new TableCell(new TableCellProperties(new TableCellWidth { Type = TableWidthUnitValues.Dxa, Width = "2800" }), Para(label, bold: true, size: 20, font: f)),
                new TableCell(new TableCellProperties(new TableCellWidth { Type = TableWidthUnitValues.Dxa, Width = "6838" }), Para(value, size: 20, font: f))));
        yield return table;

        var earlier = false; // whether a part was printed before the transcript, which then starts on a new page
        foreach (var (part, heading) in style.Order)
        {
            if (part != "transcript") earlier |= HasContent(d, part);
            switch (part)
            {
                case "summary" when !string.IsNullOrWhiteSpace(d.Summary):
                    yield return Heading(heading, style); yield return Para(d.Summary, font: f); break;
                case "actions" when d.ActionPoints.Count > 0:
                    yield return Heading(heading, style);
                    foreach (var a in d.ActionPoints) yield return Para("•  " + a, indent: 360, font: f);
                    break;
                case "minutes" when d.MinutesLines.Count > 0:
                    yield return Heading(heading, style);
                    foreach (var line in d.MinutesLines)
                    {
                        if (line.StartsWith("- ")) yield return Para("•  " + line[2..], indent: 360, font: f);
                        else if (line.EndsWith(':')) yield return Para(line, bold: true, font: f);
                        else yield return Para(line, font: f);
                    }
                    break;
                case "tone" when !string.IsNullOrWhiteSpace(d.Tone):
                    yield return Heading(heading, style); yield return Para(d.Tone, font: f); break;
                case "transcript" when d.Transcript.Count > 0:
                    yield return Heading(d.TranscriptHeading, style, newPage: earlier);
                    foreach (var p in d.Transcript)
                    {
                        var para = new Paragraph(ListOrPlain(p.Marker, "120"));
                        if (p.Marker is not null) para.Append(Run(p.Marker, font: f), new DocumentFormat.OpenXml.Wordprocessing.Run(new TabChar()));
                        if (p.Time.Length > 0) para.Append(Run(p.Time + "  ", size: 18, color: "666666", font: f));
                        if (p.Speaker.Length > 0) para.Append(Run(p.Speaker + ": ", bold: true, font: f));
                        para.Append(Run(p.Text, font: f));
                        yield return para;
                    }
                    break;
            }
        }
    }

    /// <summary>Whether the document has something to print for a part.</summary>
    internal static bool HasContent(MinutesDocument d, string part) => part switch
    {
        "summary" => !string.IsNullOrWhiteSpace(d.Summary),
        "actions" => d.ActionPoints.Count > 0,
        "minutes" => d.MinutesLines.Count > 0,
        "tone" => !string.IsNullOrWhiteSpace(d.Tone),
        "transcript" => d.Transcript.Count > 0,
        _ => false,
    };

    private static Paragraph Heading(string text, MinutesStyle style, bool newPage = false) =>
        new(newPage ? new ParagraphProperties(new KeepNext(), new PageBreakBefore(), new SpacingBetweenLines { Before = "0", After = "120" })
                : new ParagraphProperties(new KeepNext(), new SpacingBetweenLines { Before = "320", After = "120" }),
            Run(text, bold: true, size: 28, color: style.Hex == "000000" ? null : style.Hex, font: style.Font));

    private static Paragraph Para(string text, bool bold = false, int size = 22, string? color = null, int indent = 0, string? font = null)
    {
        var props = new ParagraphProperties(new SpacingBetweenLines { After = "80" });
        if (indent > 0) props.Append(new Indentation { Left = indent.ToString() });
        return new Paragraph(props, Run(text, bold, size, color, font));
    }

    private static Run Run(string text, bool bold = false, int size = 22, string? color = null, string? font = null)
    {
        var rp = new RunProperties();
        if (font is not null) rp.Append(new RunFonts { Ascii = font, HighAnsi = font, ComplexScript = font } /* Chinese and Japanese: Word picks its own East Asian font */);
        if (bold) rp.Append(new Bold());
        if (color is not null) rp.Append(new Color { Val = color });
        rp.Append(new FontSize { Val = size.ToString() });
        // Text is always inserted as plain text, so nothing in a transcript can turn into formatting or fields.
        return new Run(rp, new Text(Clean(text)) { Space = SpaceProcessingModeValues.Preserve });
    }

    /// <summary>Characters Word cannot store (control characters) are dropped.</summary>
    private static string Clean(string s) => new(s.Where(c => c >= ' ' || c == '\t').ToArray());

    /// <summary>A list point hangs its marker ("•", "1.") to the left of the text, so wrapped lines line up under the text.</summary>
    internal static ParagraphProperties ListOrPlain(string? marker, string after)
    {
        var props = new ParagraphProperties(new SpacingBetweenLines { After = marker is null ? after : "60" });
        if (marker is not null) props.Append(new Indentation { Left = "425", Hanging = "425" });
        return props;
    }
}
