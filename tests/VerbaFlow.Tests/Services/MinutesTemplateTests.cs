using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using PdfSharp.Pdf.IO;
using VerbaFlow.Core.Domain;
using VerbaFlow.Infrastructure.Export;
using VerbaFlow.Infrastructure.Services;

namespace VerbaFlow.Tests.Services;

public class MinutesTemplateTests
{
    private static MinutesTemplateInput Input(string name = "House style", bool onLetterhead = false, bool times = true,
        IReadOnlyList<TemplateSection>? sections = null, string font = "Times New Roman", bool isDefault = false) =>
        new(name, "Minutes: {name}", font, "#1F4FD1", "right", onLetterhead,
            sections ?? [new("minutes", "Discussion", true), new("actions", "Next steps", true), new("summary", "Overview", true),
                new("tone", "Tone", false), new("transcript", "Word for word", false)],
            times, "Confidential", isDefault);

    private static List<string> Paragraphs(byte[] docx, out WordprocessingDocument doc)
    {
        doc = WordprocessingDocument.Open(new MemoryStream(docx), false);
        return doc.MainDocumentPart!.Document!.Body!.Elements<Paragraph>().Select(p => p.InnerText).ToList();
    }

    [Fact]
    public async Task Only_admins_make_templates_names_are_unique_and_the_first_one_becomes_the_default()
    {
        using var env = new Env();
        var t = new MinutesTemplateService(env.Stores, env.Clock);
        await Assert.ThrowsAsync<ForbiddenException>(() => t.CreateAsync(env.Alice, Input()));
        var first = await t.CreateAsync(env.Carol, Input());
        Assert.True(first.IsDefault);
        Assert.Equal("#1F4FD1", first.Colour);
        await Assert.ThrowsAsync<DomainException>(() => t.CreateAsync(env.Carol, Input(name: "house STYLE")));
        await Assert.ThrowsAsync<DomainException>(() => t.CreateAsync(env.Carol, Input(name: "VerbaFlow standard")));
        await Assert.ThrowsAsync<DomainException>(() => t.CreateAsync(env.Carol, Input(name: "None ticked",
            sections: MinutesTemplate.StandardSections.Select(s => s with { On = false }).ToList())));
        await Assert.ThrowsAsync<DomainException>(() => t.CreateAsync(env.Carol, Input(name: "Missing a part",
            sections: MinutesTemplate.StandardSections.Take(4).ToList())));
        await Assert.ThrowsAsync<DomainException>(() => t.CreateAsync(env.Carol, Input(name: "Odd font", font: "Comic Sans")));

        // Making another the default takes it off the first.
        var second = await t.CreateAsync(env.Carol, Input(name: "Board", isDefault: true));
        Assert.True(second.IsDefault);
        Assert.False((await env.Stores.MinutesTemplates.GetAsync(first.Id))!.IsDefault);
        Assert.Equal("Board", (await t.PickAsync(null)).Name);
        Assert.Equal("VerbaFlow standard", (await t.PickAsync(Guid.Empty)).Name);
        var history = (await env.Stores.Audit.ListAsync(null)).Where(a => a.Type.StartsWith("minutes-template.")).ToList();
        Assert.Equal(2, history.Count);
        Assert.True((await env.Stores.Audit.VerifyAsync()).Ok);
    }

    [Fact]
    public async Task Minutes_follow_the_default_template_order_headings_title_font_and_footer()
    {
        using var env = new Env();
        var id = await env.ProcessedRecordingAsync();
        var t = new MinutesTemplateService(env.Stores, env.Clock);
        await t.CreateAsync(env.Carol, Input());
        var item = (await env.Stores.Items.GetAsync(id))!;

        var file = await new ExportService(env.Stores, env.Clock).ExportAsync(env.Alice, id, "docx");
        var paras = Paragraphs(file.Content, out var doc);
        using (doc)
        {
            Assert.Empty(new DocumentFormat.OpenXml.Validation.OpenXmlValidator().Validate(doc)); // Word opens it without complaint
            Assert.Equal("Minutes: " + item.Name, paras[0]);
            var discussion = paras.IndexOf("Discussion");
            var next = paras.IndexOf("Next steps");
            var overview = paras.IndexOf("Overview");
            Assert.True(discussion > 0 && discussion < next && next < overview, string.Join("|", paras));
            Assert.DoesNotContain("Word for word", paras); // switched off in the template
            Assert.DoesNotContain("Tone", paras);
            Assert.Contains("Times New Roman", doc.MainDocumentPart!.Document!.Body!.Descendants<RunFonts>().First().Ascii!.Value);
            Assert.Contains("1F4FD1", doc.MainDocumentPart.Document.Body.Descendants<Color>().Select(c => c.Val!.Value!));
            var footer = doc.MainDocumentPart.FooterParts.Single().Footer!.InnerText;
            Assert.StartsWith("Confidential · " + item.Name + " · Draft · reference ", footer);
        }
        var record = Assert.Single(await env.Stores.Exports.ListAsync());
        Assert.Equal("House style", record.Template);
        Assert.Equal("minutes,actions,summary".Split(',').OrderBy(x => x), record.Parts!.Split(',').OrderBy(x => x));
        Assert.Contains("House style", (await env.Stores.Audit.ListAsync(id)).Last().Details);

        // A part the person ticks still goes in, under the template's heading; and "standard" gives VerbaFlow's own layout.
        var withTranscript = await new ExportService(env.Stores, env.Clock).ExportAsync(env.Alice, id, "docx", parts: "summary,transcript");
        var p2 = Paragraphs(withTranscript.Content, out var d2); d2.Dispose();
        Assert.Contains("Word for word", p2);
        Assert.DoesNotContain("Discussion", p2);
        var standard = await new ExportService(env.Stores, env.Clock).ExportAsync(env.Alice, id, "docx", template: "standard");
        var p3 = Paragraphs(standard.Content, out var d3); d3.Dispose();
        Assert.Equal(item.Name, p3[0]);
        Assert.Contains("Transcript", p3);
        await Assert.ThrowsAsync<DomainException>(() => new ExportService(env.Stores, env.Clock).ExportAsync(env.Alice, id, "docx", template: Guid.NewGuid().ToString()));
    }

    [Fact]
    public async Task Times_can_be_left_out_and_a_logo_goes_on_the_first_page_in_Word_and_PDF()
    {
        using var env = new Env();
        var id = await env.ProcessedRecordingAsync();
        var t = new MinutesTemplateService(env.Stores, env.Clock);
        var made = await t.CreateAsync(env.Carol, Input(times: false, sections: MinutesTemplate.StandardSections));
        await Assert.ThrowsAsync<ForbiddenException>(() => t.SetLogoAsync(env.Alice, made.Id, new MemoryStream(SignOffTests.Png())));
        await Assert.ThrowsAsync<DomainException>(() => t.SetLogoAsync(env.Carol, made.Id, new MemoryStream("not a picture"u8.ToArray())));
        await t.SetLogoAsync(env.Carol, made.Id, new MemoryStream(SignOffTests.Png(200, 60)));

        var exports = new ExportService(env.Stores, env.Clock);
        var word = await exports.ExportAsync(env.Alice, id, "docx");
        var paras = Paragraphs(word.Content, out var doc);
        using (doc)
        {
            Assert.Single(doc.MainDocumentPart!.ImageParts);
            Assert.Empty(new DocumentFormat.OpenXml.Validation.OpenXmlValidator().Validate(doc));
            var segment = (await env.Stores.Transcripts.GetAsync(id))!.Render()[0];
            var line = paras.Single(p => p.Contains(segment.Segment.Text));
            Assert.StartsWith(segment.SpeakerName + ": ", line); // no "0:00" in front
        }
        var pdf = await exports.ExportAsync(env.Alice, id, "pdf");
        using var read = PdfReader.Open(new MemoryStream(pdf.Content), PdfDocumentOpenMode.Import);
        Assert.NotNull(read.Pages[0].Resources.Elements.GetDictionary("/XObject")); // the logo
        var sample = await t.SampleAsync(env.Carol, made.Id, "pdf");
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(sample.Content, 0, 4));
        Assert.Single(await env.Stores.Exports.ListAsync(), e => e.Format == "pdf"); // the sample is not an export
    }

    [Fact]
    public async Task On_the_letterhead_minutes_take_its_header_in_Word_and_its_page_in_PDF()
    {
        using var env = new Env();
        var id = await env.ProcessedRecordingAsync();
        var t = new MinutesTemplateService(env.Stores, env.Clock);
        await t.CreateAsync(env.Carol, Input(onLetterhead: true));
        var exports = new ExportService(env.Stores, env.Clock);

        // No letterhead uploaded yet: plain pages, and History says why.
        await exports.ExportAsync(env.Alice, id, "docx");
        Assert.Contains("none (no Word letterhead uploaded)", (await env.Stores.Audit.ListAsync(id)).Last().Details);

        var letterheads = new LetterheadService(env.Stores, env.Clock);
        await letterheads.UploadAsync(env.Carol, "word", new MemoryStream(LetterheadTests.WordLetterhead()), "acme.docx");
        await letterheads.UploadAsync(env.Carol, "pdf", new MemoryStream(LetterheadTests.PdfLetterhead()), "acme.pdf");
        var word = await exports.ExportAsync(env.Alice, id, "docx");
        using (var doc = WordprocessingDocument.Open(new MemoryStream(word.Content), false))
        {
            Assert.Contains("ACME Attorneys", string.Concat(doc.MainDocumentPart!.HeaderParts.Select(h => h.Header!.InnerText)));
            var body = doc.MainDocumentPart.Document!.Body!.InnerText;
            Assert.Contains("Discussion", body);
            Assert.DoesNotContain("Sample letter text", body);
            Assert.Contains("Confidential · ", body); // the reference line moves under the text
        }
        Assert.Contains("Word letterhead", (await env.Stores.Audit.ListAsync(id)).Last().Details);
        var pdf = await exports.ExportAsync(env.Alice, id, "pdf");
        using var read = PdfReader.Open(new MemoryStream(pdf.Content), PdfDocumentOpenMode.Import);
        Assert.Equal(612, read.Pages[0].Width.Point, 1); // the letterhead's page size
        Assert.Contains("PDF letterhead", (await env.Stores.Audit.ListAsync(id)).Last().Details);
    }
}
