using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using MigraDoc.DocumentObjectModel;
using MigraDoc.Rendering;
using PdfSharp.Drawing;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace VerbaFlow.Infrastructure.Export;

/// <summary>A dictated letter, ready to lay out: the date and reference at the top, then the paragraphs. All plain text.</summary>
/// <param name="Note">A line printed above the date when the letter is not approved ("Draft: not approved yet."), otherwise null.</param>
public sealed record LetterDocument(string Title, string Date, string? OurRef, string? Note, IReadOnlyList<string> Paragraphs);

/// <summary>The space a PDF letterhead leaves for the letter, in millimetres from the edges of the page.</summary>
public sealed record LetterSpace(int TopFirstMm, int TopNextMm, int BottomMm, int SideMm);

/// <summary>
/// Lays a letter out on the organisation's letterhead, or on a plain A4 page when there is none.
/// Word: the letterhead's headers, footers, page size, margins and fonts are kept and the letter goes into its body. Anything
/// typed in the letterhead's body is replaced, unless it contains {{letter}}: then the letter goes exactly there and the rest stays.
/// PDF: page 1 of the letterhead is drawn behind the first page of the letter, page 2 (if it has one) behind the pages after.
/// </summary>
public static class LetterWriter
{
    public const string Placeholder = "{{letter}}";

    public static byte[] Word(LetterDocument letter, byte[]? letterhead)
    {
        var ms = new MemoryStream();
        if (letterhead is null)
        {
            using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
            {
                var main = doc.AddMainDocumentPart();
                var body = new Body();
                main.Document = new W.Document(body);
                foreach (var p in Paragraphs(letter, plain: true)) body.Append(p);
                body.Append(new SectionProperties(
                    new W.PageSize { Width = 11906, Height = 16838 }, // A4
                    new PageMargin { Top = 1418, Bottom = 1418, Left = 1418, Right = 1418, Header = 709, Footer = 709 }));
                main.Document.Save();
            }
            return ms.ToArray(); // only complete once the document is closed
        }

        ms.Write(letterhead);
        ms.Position = 0;
        using (var doc = WordprocessingDocument.Open(ms, true))
        {
            if (doc.DocumentType == WordprocessingDocumentType.Template) doc.ChangeDocumentType(WordprocessingDocumentType.Document);
            var body = doc.MainDocumentPart!.Document!.Body!;
            var spot = body.Descendants<W.Paragraph>().FirstOrDefault(p => p.InnerText.Contains(Placeholder, StringComparison.OrdinalIgnoreCase));
            if (spot is not null)
            {
                foreach (var p in Paragraphs(letter, plain: false)) spot.InsertBeforeSelf(p);
                spot.Remove();
            }
            else
            {
                // Keep only the page set-up (it points at the headers and footers); the letter replaces the rest.
                var setup = body.Elements<SectionProperties>().LastOrDefault();
                body.RemoveAllChildren();
                foreach (var p in Paragraphs(letter, plain: false)) body.Append(p);
                if (setup is not null) body.Append(setup);
            }
            doc.MainDocumentPart.Document.Save();
        }
        return ms.ToArray();
    }

    /// <param name="plain">True without a letterhead: VerbaFlow then sets the text size itself; otherwise the letterhead's own style is used.</param>
    private static IEnumerable<W.Paragraph> Paragraphs(LetterDocument d, bool plain)
    {
        if (d.Note is not null) yield return Para(d.Note, plain, right: false, after: 240, size: 18, color: "B3261E", bold: true);
        yield return Para(d.Date, plain, right: true, after: d.OurRef is null ? 360 : 0);
        if (d.OurRef is not null) yield return Para("Our ref: " + d.OurRef, plain, right: true, after: 360);
        foreach (var p in d.Paragraphs) yield return Para(p, plain, right: false, after: 200);
    }

    private static W.Paragraph Para(string text, bool plain, bool right, int after, int? size = null, string? color = null, bool bold = false)
    {
        var props = new ParagraphProperties(new SpacingBetweenLines { After = after.ToString() });
        if (right) props.Append(new Justification { Val = JustificationValues.Right });
        var rp = new RunProperties();
        if (bold) rp.Append(new Bold());
        if (color is not null) rp.Append(new W.Color { Val = color });
        if (size is not null || plain) rp.Append(new FontSize { Val = (size ?? 22).ToString() });
        // Plain text only: nothing dictated can become a field or formatting.
        return new W.Paragraph(props, new W.Run(rp, new W.Text(PdfMinutesWriter.Clean(text)) { Space = SpaceProcessingModeValues.Preserve }));
    }

    public static byte[] Pdf(LetterDocument letter, byte[]? letterhead, LetterSpace space)
    {
        PdfMinutesWriter.EnsureFonts();
        var doc = new MigraDoc.DocumentObjectModel.Document();
        doc.Info.Title = letter.Title;
        var normal = doc.Styles[StyleNames.Normal]!;
        normal.Font.Name = PdfMinutesWriter.Font;
        normal.Font.Size = 11;
        normal.ParagraphFormat.SpaceAfter = Unit.FromPoint(8);

        using var form = letterhead is null ? null : XPdfForm.FromStream(new MemoryStream(letterhead));
        var section = doc.AddSection();
        section.PageSetup = doc.DefaultPageSetup.Clone();
        if (form is null)
        {
            section.PageSetup.PageFormat = PageFormat.A4;
            section.PageSetup.TopMargin = section.PageSetup.BottomMargin = Unit.FromCentimeter(2.5);
            section.PageSetup.LeftMargin = section.PageSetup.RightMargin = Unit.FromCentimeter(2.5);
        }
        else
        {
            // The letter's pages are the letterhead's size, so it fits exactly behind them.
            form.PageNumber = 1;
            section.PageSetup.PageWidth = Unit.FromPoint(form.PointWidth);
            section.PageSetup.PageHeight = Unit.FromPoint(form.PointHeight);
            section.PageSetup.TopMargin = Unit.FromMillimeter(space.TopNextMm);
            section.PageSetup.BottomMargin = Unit.FromMillimeter(space.BottomMm);
            section.PageSetup.LeftMargin = section.PageSetup.RightMargin = Unit.FromMillimeter(space.SideMm);
            // The first page usually has more printed at the top (the logo and address): start the letter lower there.
            var extra = space.TopFirstMm - space.TopNextMm;
            if (extra > 0)
            {
                var spacer = section.AddParagraph();
                spacer.Format.SpaceAfter = 0;
                spacer.Format.LineSpacingRule = LineSpacingRule.Exactly;
                spacer.Format.LineSpacing = Unit.FromMillimeter(extra);
            }
        }

        if (letter.Note is not null)
        {
            var n = section.AddParagraph();
            var t = n.AddFormattedText(PdfMinutesWriter.Clean(letter.Note), TextFormat.Bold);
            t.Font.Size = 9; t.Font.Color = new MigraDoc.DocumentObjectModel.Color(179, 38, 30);
            n.Format.SpaceAfter = Unit.FromPoint(12);
        }
        var date = section.AddParagraph(PdfMinutesWriter.Clean(letter.Date));
        date.Format.Alignment = ParagraphAlignment.Right;
        date.Format.SpaceAfter = letter.OurRef is null ? Unit.FromPoint(18) : 0;
        if (letter.OurRef is not null)
        {
            var r = section.AddParagraph("Our ref: " + PdfMinutesWriter.Clean(letter.OurRef));
            r.Format.Alignment = ParagraphAlignment.Right;
            r.Format.SpaceAfter = Unit.FromPoint(18);
        }
        foreach (var p in letter.Paragraphs) section.AddParagraph(PdfMinutesWriter.Clean(p));

        var renderer = new PdfDocumentRenderer { Document = doc };
        renderer.RenderDocument();
        var pdf = renderer.PdfDocument;
        if (form is not null)
            for (var i = 0; i < pdf.PageCount; i++)
            {
                var page = pdf.Pages[i];
                form.PageNumber = i == 0 || form.PageCount < 2 ? 1 : 2;
                using var g = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Prepend); // behind the letter's text
                g.DrawImage(form, 0, 0, page.Width.Point, page.Height.Point);
            }
        using var ms = new MemoryStream();
        pdf.Save(ms, false);
        return ms.ToArray();
    }
}
