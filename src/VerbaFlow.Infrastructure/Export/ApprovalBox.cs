using System.Globalization;
using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;
using MigraDoc.Rendering;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Annotations;

namespace VerbaFlow.Infrastructure.Export;

/// <summary>Who approved an exported document and when, for the box printed on approved PDFs.</summary>
public sealed record ApprovalStamp(string ApprovedBy, string ApprovedOn);

/// <summary>
/// The approval box on an approved PDF: a small framed box saying who approved it and when. Its last line is left empty
/// for the signature: <see cref="PdfSigner"/> puts the visible digital signature there, so the line "Digitally signed…"
/// appears only when the file really was signed, and clicking it opens the reader's signature panel. The writers mark
/// where the box landed in the PDF's information (<see cref="InfoKey"/>), which the signer reads and removes.
/// </summary>
public static class ApprovalBox
{
    internal const string InfoKey = "/VerbaFlowApprovalBox";
    public const double WidthMm = 95;
    internal const double SignatureLinePt = 15;
    private static readonly Color Green = new(30, 123, 52);

    /// <summary>Adds the box to the section (kept on one page); returns the table so its place can be found after layout.</summary>
    public static Table Add(Section section, ApprovalStamp stamp, bool right, bool heading = true)
    {
        var table = section.AddTable();
        table.Borders.Width = 0.75;
        table.Borders.Color = Green;
        table.Rows.Alignment = right ? RowAlignment.Right : RowAlignment.Left;
        table.KeepTogether = true;
        table.AddColumn(Unit.FromMillimeter(WidthMm));
        table.Format.KeepWithNext = true;

        if (heading) // minutes already say APPROVED at the top, so their box leaves it out
        {
            var head = table.AddRow();
            head.Borders.Bottom.Visible = false;
            head.TopPadding = Unit.FromPoint(4);
            head.KeepWith = 2;
            var hp = head.Cells[0].AddParagraph();
            hp.Format.SpaceAfter = 0;
            var ht = hp.AddFormattedText("APPROVED", TextFormat.Bold);
            ht.Font.Size = 10; ht.Font.Color = Green;
        }

        var by = table.AddRow();
        if (heading) by.Borders.Top.Visible = false; else by.TopPadding = Unit.FromPoint(4);
        by.Borders.Bottom.Visible = false;
        var bp = by.Cells[0].AddParagraph($"Approved by {PdfMinutesWriter.Clean(stamp.ApprovedBy)} on {PdfMinutesWriter.Clean(stamp.ApprovedOn)}.");
        bp.Format.Font.Size = 9; bp.Format.SpaceAfter = 0;

        var sig = table.AddRow(); // filled in by the signer
        sig.Borders.Top.Visible = false;
        sig.HeightRule = RowHeightRule.Exactly;
        sig.Height = Unit.FromPoint(SignatureLinePt);
        sig.Cells[0].AddParagraph().Format.SpaceAfter = 0;

        var after = section.AddParagraph();
        after.Format.SpaceAfter = Unit.FromPoint(6);
        return table;
    }

    /// <summary>After rendering: writes where the box's signature line is (page, then x, y, width, height in PDF points
    /// from the bottom-left corner) into the PDF's information, for the signer.</summary>
    public static void Mark(PdfDocumentRenderer renderer, Table table)
    {
        var dr = renderer.DocumentRenderer;
        for (var page = 1; page <= renderer.PdfDocument.PageCount; page++)
        {
            var info = dr.GetRenderInfoFromPage(page).FirstOrDefault(r => ReferenceEquals(r.DocumentObject, table));
            if (info is null) continue;
            var area = info.LayoutInfo.ContentArea;
            var pdfPage = renderer.PdfDocument.Pages[page - 1];
            var x = area.X.Point;
            // A right-aligned table is shifted by its free width when drawn.
            var full = area.Width.Point;
            var w = Unit.FromMillimeter(WidthMm).Point;
            if (table.Rows.Alignment == RowAlignment.Right) x += full - w;
            var bottom = area.Y.Point + area.Height.Point;
            var y = pdfPage.Height.Point - bottom; // PDF counts from the bottom
            renderer.PdfDocument.Info.Elements[InfoKey] = new PdfString(string.Join(" ",
                new[] { page - 1, x + 3, y + 1.5, w - 6, SignatureLinePt - 3 }.Select(v => Convert.ToDouble(v).ToString("0.##", CultureInfo.InvariantCulture))));
            return;
        }
    }

    /// <summary>Reads (and removes) the place the writer left for the visible signature, or null when there is none.</summary>
    internal static (int Page, XRect Rect)? Take(PdfDocument document)
    {
        if (document.Info.Elements.GetString(InfoKey) is not { Length: > 0 } s) return null;
        document.Info.Elements.Remove(InfoKey);
        var v = s.Split(' ').Select(x => double.Parse(x, CultureInfo.InvariantCulture)).ToArray();
        if (v.Length != 5 || v[0] < 0 || v[0] >= document.PageCount) return null;
        return ((int)v[0], new XRect(v[1], v[2], v[3], v[4]));
    }

    /// <summary>Draws the signature line inside the box: the words show only when the file was signed.</summary>
    internal sealed class SignatureLine(string text) : IAnnotationAppearanceHandler
    {
        public void DrawAppearance(XGraphics gfx, XRect rect)
        {
            var font = new XFont(PdfMinutesWriter.Font, 7.5);
            gfx.DrawString(text, font, new XSolidBrush(XColor.FromArgb(30, 123, 52)), new XRect(0, 0, rect.Width, rect.Height), XStringFormats.CenterLeft); // the appearance has its own origin at the box
        }
    }
}
