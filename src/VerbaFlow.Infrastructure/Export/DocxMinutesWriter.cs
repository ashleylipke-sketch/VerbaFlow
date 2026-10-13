using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace VerbaFlow.Infrastructure.Export;

/// <summary>Lays a <see cref="MinutesDocument"/> out as a Word file.</summary>
public static class DocxMinutesWriter
{
    public static byte[] Write(MinutesDocument d)
    {
        using var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            var main = doc.AddMainDocumentPart();
            var body = new Body();
            main.Document = new Document(body);

            var footerPart = main.AddNewPart<FooterPart>();
            footerPart.Footer = new Footer(Para(d.Footer, size: 16, color: "666666"));
            var footerId = main.GetIdOfPart(footerPart);

            body.Append(Para(d.Title, bold: true, size: 36));
            body.Append(Para(d.Marker.ToUpperInvariant(), bold: true, size: 24, color: d.Marker == "Approved" ? "1E7B34" : "B3261E"));
            body.Append(Para(d.MarkerLine, size: 20, color: "444444"));

            var table = new Table(new TableProperties(
                new TableWidth { Type = TableWidthUnitValues.Dxa, Width = "9638" },
                new TableBorders(new TopBorder { Val = BorderValues.Single, Size = 4, Color = "DDDDDD" },
                    new BottomBorder { Val = BorderValues.Single, Size = 4, Color = "DDDDDD" },
                    new InsideHorizontalBorder { Val = BorderValues.Single, Size = 4, Color = "DDDDDD" }),
                new TableLayout { Type = TableLayoutValues.Fixed }),
                new TableGrid(new GridColumn { Width = "2800" }, new GridColumn { Width = "6838" }));
            foreach (var (label, value) in d.Details)
                table.Append(new TableRow(
                    new TableCell(new TableCellProperties(new TableCellWidth { Type = TableWidthUnitValues.Dxa, Width = "2800" }), Para(label, bold: true, size: 20)),
                    new TableCell(new TableCellProperties(new TableCellWidth { Type = TableWidthUnitValues.Dxa, Width = "6838" }), Para(value, size: 20))));
            body.Append(table);

            if (!string.IsNullOrWhiteSpace(d.Summary)) { body.Append(Heading("Summary")); body.Append(Para(d.Summary)); }
            if (d.ActionPoints.Count > 0)
            {
                body.Append(Heading("Action points"));
                foreach (var a in d.ActionPoints) body.Append(Para("•  " + a, indent: 360));
            }
            if (d.MinutesLines.Count > 0)
            {
                body.Append(Heading("Minutes"));
                foreach (var line in d.MinutesLines)
                {
                    if (line.StartsWith("- ")) body.Append(Para("•  " + line[2..], indent: 360));
                    else if (line.EndsWith(':')) body.Append(Para(line, bold: true));
                    else body.Append(Para(line));
                }
            }
            if (!string.IsNullOrWhiteSpace(d.Tone)) { body.Append(Heading("Tone of the meeting")); body.Append(Para(d.Tone)); }

            if (d.Transcript.Count > 0) body.Append(Heading(d.TranscriptHeading));
            foreach (var p in d.Transcript)
            {
                var para = new Paragraph(ListOrPlain(p.Marker, "120"));
                if (p.Marker is not null) para.Append(Run(p.Marker), new DocumentFormat.OpenXml.Wordprocessing.Run(new TabChar()));
                if (p.Time.Length > 0) para.Append(Run(p.Time + "  ", size: 18, color: "666666"));
                if (p.Speaker.Length > 0) para.Append(Run(p.Speaker + ": ", bold: true));
                para.Append(Run(p.Text));
                body.Append(para);
            }

            body.Append(new SectionProperties(
                new FooterReference { Type = HeaderFooterValues.Default, Id = footerId },
                new PageSize { Width = 11906, Height = 16838 }, // A4
                new PageMargin { Top = 1134, Bottom = 1134, Left = 1134, Right = 1134, Header = 567, Footer = 567 }));
            main.Document.Save();
        }
        return ms.ToArray();
    }

    private static Paragraph Heading(string text) =>
        new(new ParagraphProperties(new SpacingBetweenLines { Before = "320", After = "120" }), Run(text, bold: true, size: 28));

    private static Paragraph Para(string text, bool bold = false, int size = 22, string? color = null, int indent = 0)
    {
        var props = new ParagraphProperties(new SpacingBetweenLines { After = "80" });
        if (indent > 0) props.Append(new Indentation { Left = indent.ToString() });
        return new Paragraph(props, Run(text, bold, size, color));
    }

    private static Run Run(string text, bool bold = false, int size = 22, string? color = null)
    {
        var rp = new RunProperties();
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
