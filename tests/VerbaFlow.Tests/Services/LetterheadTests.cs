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
    internal static byte[] WordLetterhead(string bodyText = "Sample letter text")
    {
        var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            var main = doc.AddMainDocumentPart();
            var header = main.AddNewPart<HeaderPart>();
            header.Header = new Header(new Paragraph(new Run(new Text("ACME Attorneys, 1 Long Street"))));
            main.Document = new Document(new Body(
                new Paragraph(new Run(new Text("Kept above"))),
                new Paragraph(new Run(new Drawing()), new Run(new Text("Logo caption"))), // a logo placed in the body
                new Paragraph(new Run(new Text(bodyText))),
                new SectionProperties(new HeaderReference { Type = HeaderFooterValues.Default, Id = main.GetIdOfPart(header) },
                    new PageSize { Width = 12240, Height = 15840 })));
            main.Document.Save();
        }
        return ms.ToArray();
    }

    /// <summary>A two-page PDF letterhead in US Letter size.</summary>
    internal static byte[] PdfLetterhead()
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
        Assert.DoesNotContain("Logo caption", body);
        Assert.EndsWith("Dear Mr Smith,|Thank you for your letter.", body);
        using var doc = WordprocessingDocument.Open(new MemoryStream(file.Content), false);
        Assert.Equal("12240", doc.MainDocumentPart!.Document!.Body!.Elements<SectionProperties>().Single().GetFirstChild<PageSize>()!.Width!.ToString());
        Assert.Single(doc.MainDocumentPart.Document.Body.Descendants<Drawing>()); // the logo in the body stays
        Assert.IsType<SectionProperties>(doc.MainDocumentPart.Document.Body.LastChild);
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
        Assert.StartsWith("Kept above|Logo caption|Draft: not approved yet.|", body); // with a placeholder the rest of the body stays as it is
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
    public async Task Word_documents_and_emails_also_go_on_the_letterhead_with_their_title_or_subject()
    {
        using var env = new Env();
        var letterheads = new LetterheadService(env.Stores, env.Clock);
        await letterheads.UploadAsync(env.Carol, "word", new MemoryStream(WordLetterhead()), "acme.docx");
        await new SignOffService(env.Stores, env.Clock).SaveAsync(env.Alice, true, "Kind regards,", "Alice Author", null);
        env.Speech.Heard = "Hello team full stop";
        var docId = await env.Meeting.CreateRecordingAsync(env.Alice, env.Dictation("Office move plan") with { DocumentType = "document", ClientReference = "OM-1" });
        await env.Processing.ProcessAsync(docId);
        var mailId = await env.Meeting.CreateRecordingAsync(env.Alice, env.Dictation("Friday's meeting") with { DocumentType = "email" });
        await env.Processing.ProcessAsync(mailId);
        var exports = new ExportService(env.Stores, env.Clock);

        string Text(byte[] docx, out string header)
        {
            using var d = WordprocessingDocument.Open(new MemoryStream(docx), false);
            header = string.Concat(d.MainDocumentPart!.HeaderParts.Select(h => h.Header!.InnerText));
            return d.MainDocumentPart.Document!.Body!.InnerText;
        }
        var doc = Text((await exports.ExportAsync(env.Alice, docId, "docx")).Content, out var h1);
        Assert.Contains("ACME Attorneys", h1);
        Assert.Contains("Office move plan", doc);
        Assert.Contains("Reference: OM-1", doc);
        Assert.DoesNotContain("Kind regards", doc); // no sign-off on a Word document
        var mail = Text((await exports.ExportAsync(env.Alice, mailId, "docx")).Content, out var h2);
        Assert.Contains("ACME Attorneys", h2);
        Assert.Contains("Subject: Friday's meeting", mail);
        Assert.Contains("Kind regards,", mail);
        Assert.Contains("Word letterhead", (await env.Stores.Audit.ListAsync(mailId)).Last(e => e.Type == "item.exported").Details);
        var pdf = await exports.ExportAsync(env.Alice, docId, "pdf");
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(pdf.Content, 0, 4));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_one_page_pdf_letterhead_is_on_the_first_page_only_unless_set_to_repeat(bool repeat)
    {
        var onePage = new PdfDocument();
        var lp = onePage.AddPage();
        using (var g = XGraphics.FromPdfPage(lp)) g.DrawRectangle(XBrushes.SteelBlue, 0, 0, 200, 80);
        var ms = new MemoryStream();
        onePage.Save(ms, false);
        var letter = new LetterDocument("Long", "1 November 2026", null, null,
            Enumerable.Repeat("A long paragraph that fills the page so the letter runs onto a second page and shows what happens there.", 60).ToList());
        var pdf = LetterWriter.Pdf(letter, ms.ToArray(), new LetterSpace(55, 25, 30, 25, repeat));
        using var read = PdfReader.Open(new MemoryStream(pdf), PdfDocumentOpenMode.Import);
        Assert.True(read.PageCount >= 2);
        Assert.NotNull(read.Pages[0].Resources.Elements.GetDictionary("/XObject"));
        Assert.Equal(repeat, read.Pages[1].Resources.Elements.GetDictionary("/XObject") is not null);
        if (repeat) // later pages keep the first page's space, so the text stays clear of the repeated letterhead
        {
            var plain = LetterWriter.Pdf(letter, ms.ToArray(), new LetterSpace(55, 25, 30, 25, false));
            using var r2 = PdfReader.Open(new MemoryStream(plain), PdfDocumentOpenMode.Import);
            Assert.True(read.PageCount >= r2.PageCount); // more room kept at the top of each page
        }
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

    [Fact]
    public async Task Spoken_list_points_come_out_as_bullets_and_numbers_in_letters_and_documents()
    {
        using var env = new Env();
        env.Speech.Heard = "Please bring colon bullet point your passport bullet point two photos end list then colon numbered point sign numbered point pay";
        var id = await env.Meeting.CreateRecordingAsync(env.Alice, env.Dictation());
        await env.Processing.ProcessAsync(id);
        var exports = new ExportService(env.Stores, env.Clock);
        var body = WordBody((await exports.ExportAsync(env.Alice, id, "docx")).Content, out _);
        Assert.Contains("|Please bring:|Your passport|Two photos|Then:|Sign|Pay", body); // Word draws the bullets and numbers itself
        using (var doc = WordprocessingDocument.Open(new MemoryStream((await exports.ExportAsync(env.Alice, id, "docx")).Content), false))
        {
            Assert.Equal(4, doc.MainDocumentPart!.Document!.Body!.Descendants<Indentation>().Count(x => x.Hanging?.Value == "425"));
            Assert.Equal(["bullet", "bullet", "decimal", "decimal"], WordListFormats(doc));
        }
        using var pdf = PdfReader.Open(new MemoryStream((await exports.ExportAsync(env.Alice, id, "pdf")).Content), PdfDocumentOpenMode.Import);
        Assert.Equal(1, pdf.PageCount);
        var passages = ExportService.DictationText((await env.Stores.Transcripts.GetAsync(id))!);
        Assert.Equal([null, "•", "•", null, "1.", "2."], passages.Select(p => p.Marker));
    }

    /// <summary>For each list point in the body, the format Word numbers it with ("bullet", "decimal"), read through the
    /// numbering part as Word does, plus the number it starts at when the list instance restarts.</summary>
    internal static List<string> WordListFormats(WordprocessingDocument doc, bool withStart = false)
    {
        var numbering = doc.MainDocumentPart!.NumberingDefinitionsPart!.Numbering!;
        var result = new List<string>();
        foreach (var p in doc.MainDocumentPart.Document!.Body!.Descendants<Paragraph>())
        {
            if (p.ParagraphProperties?.NumberingProperties?.NumberingId?.Val?.Value is not int numId) continue;
            var instance = numbering.Elements<NumberingInstance>().Single(n => n.NumberID!.Value == numId);
            var abstractId = instance.AbstractNumId!.Val!.Value;
            var level = numbering.Elements<AbstractNum>().Single(a => a.AbstractNumberId!.Value == abstractId).Elements<Level>().Single();
            var format = level.NumberingFormat!.Val!.ToString()!;
            var start = instance.Elements<LevelOverride>().FirstOrDefault()?.StartOverrideNumberingValue?.Val?.Value;
            result.Add(withStart ? $"{format}:{numId}{(start is null ? "" : $" from {start}")}" : format);
        }
        return result;
    }

    [Fact]
    public async Task Two_numbered_lists_restart_and_a_letterhead_keeps_its_own_lists()
    {
        using var env = new Env();
        // A letterhead that already uses Word numbering (ids 0 and 1): VerbaFlow's lists must get ids of their own.
        var lh = new MemoryStream();
        lh.Write(WordLetterhead());
        using (var doc = WordprocessingDocument.Open(lh, true))
        {
            var part = doc.MainDocumentPart!.AddNewPart<NumberingDefinitionsPart>();
            part.Numbering = new Numbering(
                new AbstractNum(new Level(new NumberingFormat { Val = NumberFormatValues.UpperRoman }, new LevelText { Val = "%1." }) { LevelIndex = 0 }) { AbstractNumberId = 0 },
                new NumberingInstance(new AbstractNumId { Val = 0 }) { NumberID = 1 });
        }
        await new LetterheadService(env.Stores, env.Clock).UploadAsync(env.Carol, "word", new MemoryStream(lh.ToArray()), "lh.docx");
        env.Speech.Heard = "First colon numbered point one numbered point two end list between numbered point three bullet point four";
        var id = await env.Meeting.CreateRecordingAsync(env.Alice, env.Dictation());
        await env.Processing.ProcessAsync(id);
        var file = await new ExportService(env.Stores, env.Clock).ExportAsync(env.Alice, id, "docx");
        using var result = WordprocessingDocument.Open(new MemoryStream(file.Content), false);
        Assert.Equal(["decimal:2 from 1", "decimal:2 from 1", "decimal:3 from 1", "bullet:4"], WordListFormats(result, withStart: true));
        var numbering = result.MainDocumentPart!.NumberingDefinitionsPart!.Numbering!;
        Assert.Equal([0, 1, 2], numbering.Elements<AbstractNum>().Select(a => a.AbstractNumberId!.Value));
        // Word wants all list definitions before the instances.
        Assert.True(numbering.ChildElements.TakeWhile(c => c is AbstractNum).Count() == 3);
        Assert.Contains(result.MainDocumentPart.Document!.Body!.Descendants<Paragraph>(), p => p.InnerText == "One"
            && p.ParagraphProperties!.ParagraphMarkRunProperties is not null);
    }
}
