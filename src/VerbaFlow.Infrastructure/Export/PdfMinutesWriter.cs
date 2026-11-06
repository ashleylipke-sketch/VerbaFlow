using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;
using MigraDoc.Rendering;
using PdfSharp.Fonts;

namespace VerbaFlow.Infrastructure.Export;

/// <summary>Lays a <see cref="MinutesDocument"/> out as a PDF (A4).</summary>
public static class PdfMinutesWriter
{
    internal const string Font = "VerbaFlow Arial";
    private static readonly Lock Gate = new();
    private static bool fontsReady;

    /// <param name="letterhead">A PDF letterhead to draw behind the pages (from a minutes template), with the space it leaves; or null.</param>
    public static byte[] Write(MinutesDocument d, byte[]? letterhead = null, LetterSpace? space = null)
    {
        EnsureFonts();
        var style = d.Style ?? MinutesStyle.Standard;
        var doc = new Document();
        doc.Info.Title = d.Title;
        var normal = doc.Styles[StyleNames.Normal]!;
        normal.Font.Name = FontFor(style.Font);
        normal.Font.Size = 10;
        normal.ParagraphFormat.SpaceAfter = Unit.FromPoint(4);
        var colour = ColourOf(style.Colour);

        using var form = letterhead is null ? null : PdfSharp.Drawing.XPdfForm.FromStream(new MemoryStream(letterhead));
        var section = doc.AddSection();
        section.PageSetup = doc.DefaultPageSetup.Clone();
        if (form is null)
        {
            section.PageSetup.PageFormat = PageFormat.A4;
            section.PageSetup.TopMargin = section.PageSetup.BottomMargin = Unit.FromCentimeter(2);
            section.PageSetup.LeftMargin = section.PageSetup.RightMargin = Unit.FromCentimeter(2);
        }
        else LetterWriter.SetUpOnLetterhead(section, form, space ?? new LetterSpace(55, 25, 30, 25));

        // On a letterhead the foot of the page is the letterhead's, so the reference line goes under the text instead.
        var footerAtEnd = form is not null || style.ReferenceAtEnd;
        if (!footerAtEnd)
        {
            var foot = section.Footers.Primary.AddParagraph();
            foot.Format.Font.Size = 7.5;
            foot.Format.Font.Color = Colors.Gray;
            foot.AddText(Clean(d.Footer) + "   Page ");
            foot.AddPageField();
            foot.AddText(" of ");
            foot.AddNumPagesField();
        }

        if (style.Logo is { } logo)
        {
            var lp = section.AddParagraph();
            lp.Format.SpaceAfter = Unit.FromPoint(10);
            if (style.LogoPlace == "right") lp.Format.Alignment = ParagraphAlignment.Right;
            var (wmm, hmm) = LetterWriter.Fit(logo, MinutesStyle.LogoHeightMm, MinutesStyle.LogoMaxWidthMm);
            var image = lp.AddImage("base64:" + Convert.ToBase64String(logo));
            image.Width = Unit.FromMillimeter(wmm);
            image.Height = Unit.FromMillimeter(hmm);
        }
        Text(section, d.Title, bold: true, size: 18, color: colour);
        Text(section, d.Marker.ToUpperInvariant(), bold: true, size: 12, color: d.Marker == "Approved" ? new Color(30, 123, 52) : new Color(179, 38, 30));
        Text(section, d.MarkerLine, size: 9, color: Colors.DimGray);

        var width = section.PageSetup.PageWidth - section.PageSetup.LeftMargin - section.PageSetup.RightMargin;
        var table = section.AddTable();
        table.Borders.Bottom.Width = 0.5;
        table.Borders.Bottom.Color = Colors.LightGray;
        table.AddColumn(Unit.FromCentimeter(4.5));
        table.AddColumn(Unit.FromPoint(Math.Max(Unit.FromCentimeter(6).Point, width.Point - Unit.FromCentimeter(4.5).Point)));
        foreach (var (label, value) in d.Details)
        {
            var row = table.AddRow();
            row.TopPadding = row.BottomPadding = Unit.FromPoint(2);
            row.Cells[0].AddParagraph().AddFormattedText(Clean(label), TextFormat.Bold);
            row.Cells[1].AddParagraph(Clean(value));
        }
        MigraDoc.DocumentObjectModel.Tables.Table? box = null;
        if (d.Approval is { } stamp)
        {
            section.AddParagraph().Format.SpaceAfter = Unit.FromPoint(4);
            box = ApprovalBox.Add(section, stamp, right: false);
        }

        var earlier = false; // whether a part was printed before the transcript, which then starts on a new page
        foreach (var (part, heading) in style.Order)
        {
            if (part != "transcript") earlier |= DocxMinutesWriter.HasContent(d, part);
            switch (part)
            {
                case "summary" when !string.IsNullOrWhiteSpace(d.Summary):
                    Heading(section, heading, colour); Text(section, d.Summary); break;
                case "actions" when d.ActionPoints.Count > 0:
                    Heading(section, heading, colour);
                    foreach (var a in d.ActionPoints) Bullet(section, a);
                    break;
                case "minutes" when d.MinutesLines.Count > 0:
                    Heading(section, heading, colour);
                    foreach (var line in d.MinutesLines)
                    {
                        if (line.StartsWith("- ")) Bullet(section, line[2..]);
                        else Text(section, line, bold: line.EndsWith(':'));
                    }
                    break;
                case "tone" when !string.IsNullOrWhiteSpace(d.Tone):
                    Heading(section, heading, colour); Text(section, d.Tone); break;
                case "transcript" when d.Transcript.Count > 0:
                    Heading(section, d.TranscriptHeading, colour, newPage: earlier);
                    foreach (var p in d.Transcript)
                    {
                        var para = section.AddParagraph();
                        para.Format.SpaceAfter = Unit.FromPoint(6);
                        if (p.Marker is not null) ListPoint(para, p.Marker);
                        if (p.Time.Length > 0)
                        {
                            var time = para.AddFormattedText(p.Time + "  ");
                            time.Font.Size = 8; time.Font.Color = Colors.Gray;
                        }
                        if (p.Speaker.Length > 0) para.AddFormattedText(Clean(p.Speaker) + ": ", TextFormat.Bold);
                        para.AddText(Clean(p.Text));
                    }
                    break;
            }
        }
        if (footerAtEnd)
        {
            var end = section.AddParagraph();
            end.Format.SpaceBefore = Unit.FromPoint(18);
            var t = end.AddFormattedText(Clean(d.Footer));
            t.Font.Size = 7.5; t.Font.Color = Colors.Gray;
        }

        var renderer = new PdfDocumentRenderer { Document = doc };
        renderer.RenderDocument();
        if (box is not null) ApprovalBox.Mark(renderer, box);
        if (form is not null) LetterWriter.DrawLetterhead(renderer.PdfDocument, form, space ?? new LetterSpace(55, 25, 30, 25));
        using var ms = new MemoryStream();
        renderer.PdfDocument.Save(ms, false);
        return ms.ToArray();
    }

    /// <summary>The PDF font name for a template font. The font file is found on the computer and embedded in the PDF.</summary>
    internal static string FontFor(string font) => "VerbaFlow " + font;

    private static Color? ColourOf(string hex)
    {
        if (hex.Length != 7 || hex == "#000000") return null;
        var v = Convert.ToInt32(hex[1..], 16);
        return new Color((byte)(v >> 16), (byte)(v >> 8 & 0xFF), (byte)(v & 0xFF));
    }

    private static void Heading(Section s, string text, Color? colour, bool newPage = false)
    {
        var p = s.AddParagraph();
        p.Format.PageBreakBefore = newPage;
        p.Format.SpaceBefore = Unit.FromPoint(newPage ? 0 : 12);
        p.Format.SpaceAfter = Unit.FromPoint(4);
        p.Format.KeepWithNext = true;
        var t = p.AddFormattedText(Clean(text), TextFormat.Bold);
        t.Font.Size = 13;
        if (colour is { } c) t.Font.Color = c;
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
            "/usr/share/fonts/truetype/crosextra", "/usr/share/fonts/truetype/msttcorefonts", "/usr/share/fonts/truetype/gelasio",
            "/usr/share/fonts/liberation", "/usr/share/fonts/dejavu", "/System/Library/Fonts/Supplemental", "/Library/Fonts",
        ];

        // For each family: regular, bold, italic, bold italic file names, best first. Each family falls back to a look-alike
        // that is free on Linux (Carlito for Calibri, Liberation Serif for Times New Roman), then to Arial's list.
        private static readonly Dictionary<string, string[][]> Families = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Arial"] =
            [
                ["arial.ttf", "Arial.ttf", "LiberationSans-Regular.ttf", "DejaVuSans.ttf"],
                ["arialbd.ttf", "Arial Bold.ttf", "LiberationSans-Bold.ttf", "DejaVuSans-Bold.ttf"],
                ["ariali.ttf", "Arial Italic.ttf", "LiberationSans-Italic.ttf", "DejaVuSans-Oblique.ttf"],
                ["arialbi.ttf", "Arial Bold Italic.ttf", "LiberationSans-BoldItalic.ttf", "DejaVuSans-BoldOblique.ttf"],
            ],
            ["Calibri"] =
            [
                ["calibri.ttf", "Carlito-Regular.ttf"], ["calibrib.ttf", "Carlito-Bold.ttf"],
                ["calibrii.ttf", "Carlito-Italic.ttf"], ["calibriz.ttf", "Carlito-BoldItalic.ttf"],
            ],
            ["Times New Roman"] =
            [
                ["times.ttf", "Times New Roman.ttf", "LiberationSerif-Regular.ttf", "DejaVuSerif.ttf"],
                ["timesbd.ttf", "Times New Roman Bold.ttf", "LiberationSerif-Bold.ttf", "DejaVuSerif-Bold.ttf"],
                ["timesi.ttf", "Times New Roman Italic.ttf", "LiberationSerif-Italic.ttf", "DejaVuSerif-Italic.ttf"],
                ["timesbi.ttf", "Times New Roman Bold Italic.ttf", "LiberationSerif-BoldItalic.ttf", "DejaVuSerif-BoldItalic.ttf"],
            ],
            ["Georgia"] =
            [
                ["georgia.ttf", "Georgia.ttf", "Gelasio-Regular.ttf"], ["georgiab.ttf", "Georgia Bold.ttf", "Gelasio-Bold.ttf"],
                ["georgiai.ttf", "Georgia Italic.ttf", "Gelasio-Italic.ttf"], ["georgiaz.ttf", "Georgia Bold Italic.ttf", "Gelasio-BoldItalic.ttf"],
            ],
            ["Verdana"] =
            [
                ["verdana.ttf", "Verdana.ttf", "DejaVuSans.ttf"], ["verdanab.ttf", "Verdana Bold.ttf", "DejaVuSans-Bold.ttf"],
                ["verdanai.ttf", "Verdana Italic.ttf", "DejaVuSans-Oblique.ttf"], ["verdanaz.ttf", "Verdana Bold Italic.ttf", "DejaVuSans-BoldOblique.ttf"],
            ],
        };

        private static readonly Dictionary<string, string> Fallback = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Georgia"] = "Times New Roman", ["Calibri"] = "Arial", ["Verdana"] = "Arial", ["Times New Roman"] = "Arial",
        };

        public FontResolverInfo? ResolveTypeface(string familyName, bool bold, bool italic)
        {
            var family = familyName.StartsWith("VerbaFlow ") ? familyName["VerbaFlow ".Length..] : familyName;
            if (!Families.ContainsKey(family)) family = "Arial"; // "VerbaFlow Sans" and anything unknown
            var style = (bold, italic) switch { (true, true) => 3, (true, false) => 1, (false, true) => 2, _ => 0 };
            return new FontResolverInfo($"{family}|{style}");
        }

        public byte[]? GetFont(string faceName)
        {
            var parts = faceName.Split('|');
            var family = parts[0];
            var style = parts.Length > 1 && int.TryParse(parts[1], out var n) ? n : 0;
            for (var f = family; f is not null; f = Fallback.GetValueOrDefault(f))
            {
                // The wanted style first, then the regular face of the same family, before trying a look-alike family.
                foreach (var name in Families[f][style].Concat(Families[f][0]))
                    foreach (var dir in Dirs.Where(d => !string.IsNullOrEmpty(d)))
                    {
                        var path = Path.Combine(dir, name);
                        if (File.Exists(path)) return File.ReadAllBytes(path);
                    }
            }
            throw new InvalidOperationException("No font for PDF export was found on this computer (looked for Arial, Liberation Sans and DejaVu Sans).");
        }
    }

    /// <summary>Hangs a list marker ("•", "1.") to the left of the text, so wrapped lines line up under the text.</summary>
    internal static void ListPoint(MigraDoc.DocumentObjectModel.Paragraph para, string marker)
    {
        var indent = Unit.FromMillimeter(7.5);
        para.Format.LeftIndent = indent;
        para.Format.FirstLineIndent = -indent;
        para.Format.TabStops.AddTabStop(indent);
        para.Format.SpaceAfter = Unit.FromPoint(3);
        para.AddText(marker);
        para.AddTab();
    }
}
