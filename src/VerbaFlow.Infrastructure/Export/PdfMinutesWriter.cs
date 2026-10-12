using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;
using MigraDoc.Rendering;
using PdfSharp.Fonts;

namespace VerbaFlow.Infrastructure.Export;

/// <summary>Lays a <see cref="MinutesDocument"/> out as a PDF (A4).</summary>
public static class PdfMinutesWriter
{
    internal const string Font = "VerbaFlow Sans";
    private static readonly Lock Gate = new();
    private static bool fontsReady;

    public static byte[] Write(MinutesDocument d)
    {
        EnsureFonts();
        var doc = new Document();
        doc.Info.Title = d.Title;
        var normal = doc.Styles[StyleNames.Normal]!;
        normal.Font.Name = Font;
        normal.Font.Size = 10;
        normal.ParagraphFormat.SpaceAfter = Unit.FromPoint(4);

        var section = doc.AddSection();
        section.PageSetup = doc.DefaultPageSetup.Clone();
        section.PageSetup.PageFormat = PageFormat.A4;
        section.PageSetup.TopMargin = section.PageSetup.BottomMargin = Unit.FromCentimeter(2);
        section.PageSetup.LeftMargin = section.PageSetup.RightMargin = Unit.FromCentimeter(2);

        var foot = section.Footers.Primary.AddParagraph();
        foot.Format.Font.Size = 7.5;
        foot.Format.Font.Color = Colors.Gray;
        foot.AddText(Clean(d.Footer) + "   Page ");
        foot.AddPageField();
        foot.AddText(" of ");
        foot.AddNumPagesField();

        Text(section, d.Title, bold: true, size: 18);
        Text(section, d.Marker.ToUpperInvariant(), bold: true, size: 12, color: d.Marker == "Approved" ? new Color(30, 123, 52) : new Color(179, 38, 30));
        Text(section, d.MarkerLine, size: 9, color: Colors.DimGray);

        var table = section.AddTable();
        table.Borders.Bottom.Width = 0.5;
        table.Borders.Bottom.Color = Colors.LightGray;
        table.AddColumn(Unit.FromCentimeter(4.5));
        table.AddColumn(Unit.FromCentimeter(12.5));
        foreach (var (label, value) in d.Details)
        {
            var row = table.AddRow();
            row.TopPadding = row.BottomPadding = Unit.FromPoint(2);
            row.Cells[0].AddParagraph().AddFormattedText(Clean(label), TextFormat.Bold);
            row.Cells[1].AddParagraph(Clean(value));
        }

        if (!string.IsNullOrWhiteSpace(d.Summary)) { Heading(section, "Summary"); Text(section, d.Summary); }
        if (d.ActionPoints.Count > 0)
        {
            Heading(section, "Action points");
            foreach (var a in d.ActionPoints) Bullet(section, a);
        }
        if (d.MinutesLines.Count > 0)
        {
            Heading(section, "Minutes");
            foreach (var line in d.MinutesLines)
            {
                if (line.StartsWith("- ")) Bullet(section, line[2..]);
                else Text(section, line, bold: line.EndsWith(':'));
            }
        }
        if (!string.IsNullOrWhiteSpace(d.Tone)) { Heading(section, "Tone of the meeting"); Text(section, d.Tone); }

        if (d.Transcript.Count > 0) Heading(section, d.TranscriptHeading);
        foreach (var p in d.Transcript)
        {
            var para = section.AddParagraph();
            para.Format.SpaceAfter = Unit.FromPoint(6);
            if (p.Time.Length > 0)
            {
                var time = para.AddFormattedText(p.Time + "  ");
                time.Font.Size = 8; time.Font.Color = Colors.Gray;
            }
            if (p.Speaker.Length > 0) para.AddFormattedText(Clean(p.Speaker) + ": ", TextFormat.Bold);
            para.AddText(Clean(p.Text));
        }

        var renderer = new PdfDocumentRenderer { Document = doc };
        renderer.RenderDocument();
        using var ms = new MemoryStream();
        renderer.PdfDocument.Save(ms, false);
        return ms.ToArray();
    }

    private static void Heading(Section s, string text)
    {
        var p = s.AddParagraph();
        p.Format.SpaceBefore = Unit.FromPoint(12);
        p.Format.SpaceAfter = Unit.FromPoint(4);
        p.Format.KeepWithNext = true;
        var t = p.AddFormattedText(text, TextFormat.Bold);
        t.Font.Size = 13;
    }

    private static void Text(Section s, string text, bool bold = false, double size = 10, Color? color = null)
    {
        var p = s.AddParagraph();
        var t = p.AddFormattedText(Clean(text), bold ? TextFormat.Bold : TextFormat.NotBold);
        t.Font.Size = size;
        if (color is { } c) t.Font.Color = c;
    }

    private static void Bullet(Section s, string text)
    {
        var p = s.AddParagraph("•  " + Clean(text));
        p.Format.LeftIndent = Unit.FromCentimeter(0.6);
        p.Format.FirstLineIndent = Unit.FromCentimeter(-0.4);
    }

    /// <summary>MigraDoc treats text as plain text already; control characters are dropped so nothing odd reaches the PDF.</summary>
    internal static string Clean(string s) => new(s.Where(c => c >= ' ' || c == '\t').ToArray());

    internal static void EnsureFonts()
    {
        lock (Gate)
        {
            if (fontsReady) return;
            if (GlobalFontSettings.FontResolver is null) GlobalFontSettings.FontResolver = new SystemFontResolver();
            fontsReady = true;
        }
    }

    /// <summary>
    /// Finds a plain sans-serif font on the computer: Arial on Windows, Liberation Sans or DejaVu Sans on Linux.
    /// The font is embedded in the PDF, so readers do not need it.
    /// </summary>
    private sealed class SystemFontResolver : IFontResolver
    {
        private static readonly string[] Dirs =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts"),
            "/usr/share/fonts/truetype/liberation", "/usr/share/fonts/truetype/liberation2", "/usr/share/fonts/truetype/dejavu",
            "/usr/share/fonts/liberation", "/usr/share/fonts/dejavu", "/System/Library/Fonts/Supplemental", "/Library/Fonts",
        ];

        private static readonly Dictionary<string, string[]> Faces = new()
        {
            ["regular"] = ["arial.ttf", "Arial.ttf", "LiberationSans-Regular.ttf", "DejaVuSans.ttf"],
            ["bold"] = ["arialbd.ttf", "Arial Bold.ttf", "LiberationSans-Bold.ttf", "DejaVuSans-Bold.ttf"],
            ["italic"] = ["ariali.ttf", "Arial Italic.ttf", "LiberationSans-Italic.ttf", "DejaVuSans-Oblique.ttf"],
            ["bolditalic"] = ["arialbi.ttf", "Arial Bold Italic.ttf", "LiberationSans-BoldItalic.ttf", "DejaVuSans-BoldOblique.ttf"],
        };

        public FontResolverInfo? ResolveTypeface(string familyName, bool bold, bool italic) =>
            new((bold, italic) switch { (true, true) => "bolditalic", (true, false) => "bold", (false, true) => "italic", _ => "regular" });

        public byte[]? GetFont(string faceName)
        {
            foreach (var name in Faces.GetValueOrDefault(faceName, Faces["regular"]).Concat(Faces["regular"]))
                foreach (var dir in Dirs.Where(d => !string.IsNullOrEmpty(d)))
                {
                    var path = Path.Combine(dir, name);
                    if (File.Exists(path)) return File.ReadAllBytes(path);
                }
            throw new InvalidOperationException("No font for PDF export was found on this computer (looked for Arial, Liberation Sans and DejaVu Sans).");
        }
    }
}
