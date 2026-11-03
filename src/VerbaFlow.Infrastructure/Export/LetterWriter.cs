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
/// <param name="Markers">"•" or "1." in front of each paragraph that is a list point (same order as Paragraphs), or null.</param>
/// <param name="SignOff">The author's closing, signature picture, name and title, printed under the letter; null for none.</param>
public sealed record LetterDocument(string Title, string Date, string? OurRef, string? Note, IReadOnlyList<string> Paragraphs,
    IReadOnlyList<string?>? Markers = null, LetterSignOff? SignOff = null);

/// <summary>The space a PDF letterhead leaves for the letter, in millimetres from the edges of the page.</summary>
/// <param name="RepeatFirstPage">With a one-page PDF letterhead: true prints it behind every page, false behind the first page only.</param>
public sealed record LetterSpace(int TopFirstMm, int TopNextMm, int BottomMm, int SideMm, bool RepeatFirstPage = false);

/// <summary>
/// Lays a letter out on the organisation's letterhead, or on a plain A4 page when there is none.
/// Word: the letterhead's headers, footers, page size, margins and fonts are kept and the letter goes into its body. Text
/// typed in the letterhead's body is replaced (pictures, shapes and text boxes there are kept), unless it contains {{letter}}: then the letter goes exactly there and the rest stays.
/// PDF: page 1 of the letterhead is drawn behind the first page of the letter, page 2 (if it has one) behind the pages after;
/// a one-page letterhead leaves later pages plain unless it is set to repeat.
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
                foreach (var p in Paragraphs(letter, plain: true, main)) body.Append(p);
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
                foreach (var p in Paragraphs(letter, plain: false, doc.MainDocumentPart)) spot.InsertBeforeSelf(p);
                spot.Remove();
            }
            else
            {
                // The letter replaces the letterhead's typed text. Kept: the page set-up (it points at the headers and footers),
                // and anything drawn in the body, because many letterheads put the logo, address box or lines there rather
                // than in the header. Plain text next to a drawing is dropped; text inside a text box stays with its box.
                var setup = body.Elements<SectionProperties>().LastOrDefault();
                foreach (var child in body.ChildElements.ToList())
                {
                    if (child is SectionProperties) continue;
                    if (!HasGraphics(child) && !child.Descendants<SectionProperties>().Any()) { child.Remove(); continue; }
                    foreach (var run in child.Descendants<W.Run>().Where(r => !HasGraphics(r) && r.Ancestors().All(a => !IsGraphic(a))).ToList()) run.Remove();
                }
                setup?.Remove();
                foreach (var p in Paragraphs(letter, plain: false, doc.MainDocumentPart)) body.Append(p);
                if (setup is not null) body.Append(setup);
            }
            doc.MainDocumentPart.Document.Save();
        }
        return ms.ToArray();
    }

    private static bool IsGraphic(OpenXmlElement e) =>
        e is W.Drawing or W.Picture or W.EmbeddedObject or AlternateContent;

    private static bool HasGraphics(OpenXmlElement e) => e.Descendants().Any(IsGraphic);

    /// <param name="plain">True without a letterhead: VerbaFlow then sets the text size itself; otherwise the letterhead's own style is used.</param>
    private static IEnumerable<W.Paragraph> Paragraphs(LetterDocument d, bool plain, MainDocumentPart main)
    {
        if (d.Note is not null) yield return Para(d.Note, plain, right: false, after: 240, size: 18, color: "B3261E", bold: true);
        yield return Para(d.Date, plain, right: true, after: d.OurRef is null ? 360 : 0);
        if (d.OurRef is not null) yield return Para("Our ref: " + d.OurRef, plain, right: true, after: 360);
        for (var i = 0; i < d.Paragraphs.Count; i++)
        {
            var marker = d.Markers is { } m && i < m.Count ? m[i] : null;
            var para = Para(d.Paragraphs[i], plain, right: false, after: 200);
            if (marker is not null)
            {
                para.ParagraphProperties = DocxMinutesWriter.ListOrPlain(marker, "200");
                var first = para.GetFirstChild<W.Run>()!;
                first.InsertBeforeSelf(new W.Run((RunProperties)first.RunProperties!.CloneNode(true), new W.Text(marker)));
                first.InsertBeforeSelf(new W.Run(new W.TabChar()));
            }
            yield return para;
        }
        if (d.SignOff is { } so)
        {
            // Kept together on one page: Word moves the closing down with the name rather than leaving it alone.
            if (so.Closing is not null) yield return Keep(Para(so.Closing, plain, right: false, after: so.Picture is null ? 840 : 60));
            if (so.Picture is not null) yield return Keep(PictureParagraph(main, so.Picture));
            yield return Keep(Para(so.Name, plain, right: false, after: 0), next: so.Title is not null);
            if (so.Title is not null) yield return Para(so.Title, plain, right: false, after: 0);
        }
    }

    /// <summary>The height a signature picture is printed at; its width follows, up to <see cref="SignatureMaxWidthMm"/>.</summary>
    public const double SignatureHeightMm = 18, SignatureMaxWidthMm = 70;

    /// <summary>The printed size of a signature picture in millimetres.</summary>
    public static (double W, double H) SignatureSize(byte[] picture)
    {
        using var img = XImage.FromStream(new MemoryStream(picture));
        var ratio = img.PixelWidth / (double)Math.Max(1, img.PixelHeight);
        var h = SignatureHeightMm;
        var w = h * ratio;
        if (w > SignatureMaxWidthMm) { w = SignatureMaxWidthMm; h = w / ratio; }
        return (w, h);
    }

    private static W.Paragraph Keep(W.Paragraph p, bool next = true)
    {
        if (next) p.ParagraphProperties!.PrependChild(new KeepNext());
        return p;
    }

    private static W.Paragraph PictureParagraph(MainDocumentPart main, byte[] picture)
    {
        var png = Services.SignOffService.PictureType(picture) == "png";
        var part = main.AddImagePart(png ? ImagePartType.Png : ImagePartType.Jpeg);
        using (var s = new MemoryStream(picture)) part.FeedData(s);
        var rel = main.GetIdOfPart(part);
        var (wmm, hmm) = SignatureSize(picture);
        long cx = (long)(wmm * 36000), cy = (long)(hmm * 36000); // EMU per millimetre
        var id = (uint)(main.Parts.Count() + 1000);
        var drawing = new W.Drawing(
            new DocumentFormat.OpenXml.Drawing.Wordprocessing.Inline(
                new DocumentFormat.OpenXml.Drawing.Wordprocessing.Extent { Cx = cx, Cy = cy },
                new DocumentFormat.OpenXml.Drawing.Wordprocessing.EffectExtent { LeftEdge = 0, TopEdge = 0, RightEdge = 0, BottomEdge = 0 },
                new DocumentFormat.OpenXml.Drawing.Wordprocessing.DocProperties { Id = id, Name = "Signature" },
                new DocumentFormat.OpenXml.Drawing.Wordprocessing.NonVisualGraphicFrameDrawingProperties(
                    new DocumentFormat.OpenXml.Drawing.GraphicFrameLocks { NoChangeAspect = true }),
                new DocumentFormat.OpenXml.Drawing.Graphic(new DocumentFormat.OpenXml.Drawing.GraphicData(
                    new DocumentFormat.OpenXml.Drawing.Pictures.Picture(
                        new DocumentFormat.OpenXml.Drawing.Pictures.NonVisualPictureProperties(
                            new DocumentFormat.OpenXml.Drawing.Pictures.NonVisualDrawingProperties { Id = 0, Name = "signature" },
                            new DocumentFormat.OpenXml.Drawing.Pictures.NonVisualPictureDrawingProperties()),
                        new DocumentFormat.OpenXml.Drawing.Pictures.BlipFill(
                            new DocumentFormat.OpenXml.Drawing.Blip { Embed = rel },
                            new DocumentFormat.OpenXml.Drawing.Stretch(new DocumentFormat.OpenXml.Drawing.FillRectangle())),
                        new DocumentFormat.OpenXml.Drawing.Pictures.ShapeProperties(
                            new DocumentFormat.OpenXml.Drawing.Transform2D(
                                new DocumentFormat.OpenXml.Drawing.Offset { X = 0, Y = 0 },
                                new DocumentFormat.OpenXml.Drawing.Extents { Cx = cx, Cy = cy }),
                            new DocumentFormat.OpenXml.Drawing.PresetGeometry(new DocumentFormat.OpenXml.Drawing.AdjustValueList())
                            { Preset = DocumentFormat.OpenXml.Drawing.ShapeTypeValues.Rectangle })))
                { Uri = "http://schemas.openxmlformats.org/drawingml/2006/picture" }))
            { DistanceFromTop = 0, DistanceFromBottom = 0, DistanceFromLeft = 0, DistanceFromRight = 0 });
        return new W.Paragraph(new ParagraphProperties(new SpacingBetweenLines { After = "60" }), new W.Run(drawing));
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
            // A one-page letterhead repeated on every page needs the first page's space at the top of every page.
            var sameOnEveryPage = space.RepeatFirstPage && form.PageCount < 2;
            var topNext = sameOnEveryPage ? Math.Max(space.TopFirstMm, space.TopNextMm) : space.TopNextMm;
            section.PageSetup.TopMargin = Unit.FromMillimeter(topNext);
            section.PageSetup.BottomMargin = Unit.FromMillimeter(space.BottomMm);
            section.PageSetup.LeftMargin = section.PageSetup.RightMargin = Unit.FromMillimeter(space.SideMm);
            // The first page usually has more printed at the top (the logo and address): start the letter lower there.
            var extra = space.TopFirstMm - topNext;
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
        for (var i = 0; i < letter.Paragraphs.Count; i++)
        {
            var para = section.AddParagraph();
            if (letter.Markers is { } m && i < m.Count && m[i] is { } marker) PdfMinutesWriter.ListPoint(para, marker);
            para.AddText(PdfMinutesWriter.Clean(letter.Paragraphs[i]));
        }
        if (letter.SignOff is { } so)
        {
            if (so.Closing is not null)
            {
                var c = section.AddParagraph(PdfMinutesWriter.Clean(so.Closing));
                c.Format.KeepWithNext = true;
                c.Format.SpaceAfter = so.Picture is null ? Unit.FromPoint(42) : Unit.FromPoint(3);
            }
            if (so.Picture is not null)
            {
                var pic = section.AddParagraph();
                pic.Format.KeepWithNext = true;
                pic.Format.SpaceAfter = Unit.FromPoint(3);
                var (wmm, hmm) = SignatureSize(so.Picture);
                var image = pic.AddImage("base64:" + Convert.ToBase64String(so.Picture));
                image.Width = Unit.FromMillimeter(wmm);
                image.Height = Unit.FromMillimeter(hmm);
            }
            var name = section.AddParagraph(PdfMinutesWriter.Clean(so.Name));
            name.Format.SpaceAfter = 0;
            name.Format.KeepWithNext = so.Title is not null;
            if (so.Title is not null) section.AddParagraph(PdfMinutesWriter.Clean(so.Title)).Format.SpaceAfter = 0;
        }

        var renderer = new PdfDocumentRenderer { Document = doc };
        renderer.RenderDocument();
        var pdf = renderer.PdfDocument;
        if (form is not null)
            for (var i = 0; i < pdf.PageCount; i++)
            {
                var page = pdf.Pages[i];
                // Later pages: the letterhead's page 2 if it has one; otherwise page 1 again only when asked, else plain.
                if (i > 0 && form.PageCount < 2 && !space.RepeatFirstPage) continue;
                form.PageNumber = i == 0 || form.PageCount < 2 ? 1 : 2;
                using var g = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Prepend); // behind the letter's text
                g.DrawImage(form, 0, 0, page.Width.Point, page.Height.Point);
            }
        using var ms = new MemoryStream();
        pdf.Save(ms, false);
        return ms.ToArray();
    }
}
