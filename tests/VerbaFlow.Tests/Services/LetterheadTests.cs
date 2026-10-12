using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using VerbaFlow.Core.Domain;
using VerbaFlow.Infrastructure.Export;
using VerbaFlow.Infrastructure.Services;

namespace VerbaFlow.Tests.Services;

public class LetterheadTests
{
    /// <summary>A Word letterhead with the firm's name in its header and some sample text in the body.</summary>
    private static byte[] WordLetterhead(string bodyText = "Sample letter text")
    {
        var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            var main = doc.AddMainDocumentPart();
            var header = main.AddNewPart<HeaderPart>();
            header.Header = new Header(new Paragraph(new Run(new Text("ACME Attorneys, 1 Long Street"))));
            main.Document = new Document(new Body(
                new Paragraph(new Run(new Text("Kept above"))),
                new Paragraph(new Run(new Text(bodyText))),
                new SectionProperties(new HeaderReference { Type = HeaderFooterValues.Default, Id = main.GetIdOfPart(header) },
                    new PageSize { Width = 12240, Height = 15840 })));
            main.Document.Save();
        }
        return ms.ToArray();
    }

    /// <summary>A two-page PDF letterhead in US Letter size.</summary>
    private static byte[] PdfLetterhead()
    {
        PdfMinutesWriterFonts();
        var pdf = new PdfDocument();
        for (var i = 0; i < 2; i++)
        {
            var page = pdf.AddPage();
            page.Width = XUnit.FromPoint(612); page.Height = XUnit.FromPoint(792);
            using var g = XGraphics.FromPdfPage(page);
            g.DrawRectangle(XBrushes.SteelBlue, 0, 0, 612, i == 0 ? 120 : 40);
        }
        var ms = new MemoryStream();
        pdf.Save(ms, false);
        return ms.ToArray();
    }

    private static void PdfMinutesWriterFonts() { } // the drawing above uses no text, so no font is needed

    private static async Task<Guid> Letter(Env env, string? reference = "ACME-042")
    {
        env.Speech.Heard = "Dear Mr Smith comma new paragraph thank you for your letter full stop";
        var id = await env.Meeting.CreateRecordingAsync(env.Alice, env.Dictation() with { ClientReference = reference });
        await env.Processing.ProcessAsync(id);
        return id;
    }

    private static string WordBody(byte[] file, out string headers)
    {
        using var doc = WordprocessingDocument.Open(new MemoryStream(file), false);
        headers = string.Concat(doc.MainDocumentPart!.HeaderParts.Select(h => h.Header!.InnerText));
        return string.Join("|", doc.MainDocumentPart.Document!.Body!.Elements<Paragraph>().Select(p => p.InnerText));
    }

    [Fact]
    public async Task A_letter_without_a_letterhead_is_laid_out_as_a_letter_with_date_and_reference()
    {
        using var env = new Env();
        var id = await Letter(env);
        var file = await new ExportService(env.Stores, env.Clock).ExportAsync(env.Alice, id, "docx");
        var body = WordBody(file.Content, out _);
        Assert.StartsWith("Draft: not approved yet.|", body);
        Assert.Contains("|Our ref: ACME-042|Dear Mr Smith,|Thank you for your letter.", body);
        Assert.DoesNotContain("Recorded by", body); // no meeting-style details table
        var pdf = await new ExportService(env.Stores, env.Clock).ExportAsync(env.Alice, id, "pdf");
        using var read = PdfReader.Open(new MemoryStream(pdf.Content), PdfDocumentOpenMode.Import);
        Assert.Equal(1, read.PageCount);
        var exported = (await env.Stores.Audit.ListAsync(null)).Where(a => a.Type == "item.exported").Select(a => a.Details).ToList();
        Assert.Contains("none (no Word letterhead uploaded)", exported[0]); // History says why the letter is plain
        Assert.Contains("none (no PDF letterhead uploaded)", exported[1]);
    }

    [Fact]
    public async Task A_word_letterhead_keeps_its_header_and_page_and_the_letter_replaces_its_body()
    {
        using var env = new Env();
        var letterheads = new LetterheadService(env.Stores, env.Clock);
        await letterheads.UploadAsync(env.Carol, "word", new MemoryStream(WordLetterhead()), "acme.docx");
        var id = await Letter(env);
        var file = await new ExportService(env.Stores, env.Clock).ExportAsync(env.Alice, id, "docx");
        var body = WordBody(file.Content, out var headers);
        Assert.Contains("ACME Attorneys, 1 Long Street", headers);
        Assert.DoesNotContain("Sample letter text", body);
        Assert.DoesNotContain("Kept above", body);
        Assert.EndsWith("Dear Mr Smith,|Thank you for your letter.", body);
        using var doc = WordprocessingDocument.Open(new MemoryStream(file.Content), false);
        Assert.Equal("12240", doc.MainDocumentPart!.Document!.Body!.Elements<SectionProperties>().Single().GetFirstChild<PageSize>()!.Width!.ToString());
        var audit = (await env.Stores.Audit.ListAsync(null)).Single(a => a.Type == "letterhead.uploaded");
        Assert.Contains("sha256", audit.Details);
        Assert.Contains("\"letterhead\":\"Word letterhead\"", (await env.Stores.Audit.ListAsync(null)).Single(a => a.Type == "item.exported").Details);
    }

    [Fact]
    public async Task A_placeholder_in_the_word_letterhead_says_exactly_where_the_letter_goes()
    {
        using var env = new Env();
        var letterheads = new LetterheadService(env.Stores, env.Clock);
        await letterheads.UploadAsync(env.Carol, "word", new MemoryStream(WordLetterhead(LetterWriter.Placeholder)), "acme.docx");
        var id = await Letter(env, reference: null);
        var body = WordBody((await new ExportService(env.Stores, env.Clock).ExportAsync(env.Alice, id, "docx")).Content, out _);
        Assert.StartsWith("Kept above|Draft: not approved yet.|", body);
        Assert.DoesNotContain("Our ref", body);
        Assert.DoesNotContain(LetterWriter.Placeholder, body);
    }

    [Fact]
    public async Task A_pdf_letterhead_sets_the_page_size_and_is_drawn_behind_every_page()
    {
        using var env = new Env();
        var letterheads = new LetterheadService(env.Stores, env.Clock);
        await letterheads.UploadAsync(env.Carol, "pdf", new MemoryStream(PdfLetterhead()), "acme.pdf");
        await letterheads.SetPdfSpaceAsync(env.Carol, 60, 30, 30, 25);
        var id = await Letter(env);
        var pdf = await new ExportService(env.Stores, env.Clock).ExportAsync(env.Alice, id, "pdf");
        using var read = PdfReader.Open(new MemoryStream(pdf.Content), PdfDocumentOpenMode.Import);
        Assert.Equal(612, read.Pages[0].Width.Point, 1);
        Assert.Equal(792, read.Pages[0].Height.Point, 1);
        var xobjects = read.Pages[0].Resources.Elements.GetDictionary("/XObject");
        Assert.NotNull(xobjects); // the letterhead page, drawn as a form
    }

    [Fact]
    public async Task Only_administrators_change_the_letterhead_and_bad_files_are_refused_in_plain_words()
    {
        using var env = new Env();
        var letterheads = new LetterheadService(env.Stores, env.Clock);
        await Assert.ThrowsAsync<ForbiddenException>(() => letterheads.UploadAsync(env.Alice, "word", new MemoryStream(WordLetterhead()), "a.docx"));
        var notWord = await Assert.ThrowsAsync<DomainException>(() => letterheads.UploadAsync(env.Carol, "word", new MemoryStream("hello"u8.ToArray()), "a.docx"));
        Assert.Contains("not a Word file", notWord.Message);
        await Assert.ThrowsAsync<DomainException>(() => letterheads.UploadAsync(env.Carol, "pdf", new MemoryStream("%PDF-nonsense"u8.ToArray()), "a.pdf"));
        await Assert.ThrowsAsync<DomainException>(() => letterheads.SetPdfSpaceAsync(env.Carol, 1, 25, 25, 25));
        await letterheads.UploadAsync(env.Carol, "word", new MemoryStream(WordLetterhead()), "a.docx");
        Assert.NotNull((await letterheads.GetAsync()).Word);
        await Assert.ThrowsAsync<ForbiddenException>(() => letterheads.RemoveAsync(env.Bob, "word"));
        Assert.Null((await letterheads.RemoveAsync(env.Dave, "word")).Word);
        Assert.Contains("letterhead.removed", (await env.Stores.Audit.ListAsync(null)).Select(a => a.Type));
    }
}
