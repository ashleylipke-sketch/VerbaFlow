using System.IO.Compression;
using DocumentFormat.OpenXml.Packaging;
using VerbaFlow.Core.Domain;
using VerbaFlow.Infrastructure.Export;
using VerbaFlow.Infrastructure.Services;

namespace VerbaFlow.Tests.Services;

public class SignOffTests
{
    /// <summary>A small PNG (wide, like a signature), made by hand so the test needs no drawing library.</summary>
    internal static byte[] Png(int w = 120, int h = 40)
    {
        static byte[] Chunk(string type, byte[] data)
        {
            var t = System.Text.Encoding.ASCII.GetBytes(type);
            var crc = Crc([.. t, .. data]);
            return [.. BitConverter.GetBytes(data.Length).Reverse(), .. t, .. data, .. BitConverter.GetBytes(crc).Reverse()];
        }
        var raw = new MemoryStream();
        for (var y = 0; y < h; y++) { raw.WriteByte(0); for (var x = 0; x < w; x++) raw.WriteByte((byte)((x + y) % 7 == 0 ? 0 : 255)); }
        var z = new MemoryStream();
        using (var zs = new ZLibStream(z, CompressionLevel.Optimal, true)) zs.Write(raw.ToArray());
        byte[] ihdr = [.. BitConverter.GetBytes(w).Reverse(), .. BitConverter.GetBytes(h).Reverse(), 8, 0, 0, 0, 0];
        return [0x89, (byte)'P', (byte)'N', (byte)'G', 13, 10, 26, 10, .. Chunk("IHDR", ihdr), .. Chunk("IDAT", z.ToArray()), .. Chunk("IEND", [])];
    }

    private static uint Crc(byte[] bytes)
    {
        var c = 0xFFFFFFFFu;
        foreach (var b in bytes) { c ^= b; for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1; }
        return ~c;
    }

    private static SignOff Mine(bool on = true) => new(Guid.NewGuid(), on, "Yours sincerely,", "Alice Author", "Partner", null, null, default);

    [Fact]
    public void A_letter_without_a_closing_gets_the_whole_sign_off()
    {
        var f = SignOffLayout.Fit(["Dear Mr Smith,", "Thank you."], null, Mine(), Png(), approved: true);
        Assert.Equal(["Dear Mr Smith,", "Thank you."], f.Paragraphs);
        Assert.Equal("Yours sincerely,", f.SignOff!.Closing);
        Assert.NotNull(f.SignOff.Picture);
        Assert.Equal("Partner", f.SignOff.Title);
        Assert.Equal("added, with signature picture", f.Note);
    }

    [Fact]
    public void A_dictated_closing_is_kept_and_the_name_said_after_it_gives_way_to_the_sign_off()
    {
        var f = SignOffLayout.Fit(["Dear Sir,", "Thank you.", "Yours faithfully.", "Alice"], [null, null, null, null], Mine(), Png(), approved: false);
        Assert.Equal(["Dear Sir,", "Thank you."], f.Paragraphs);
        Assert.Equal(2, f.Markers!.Count);
        Assert.Equal("Yours faithfully.", f.SignOff!.Closing);
        Assert.Null(f.SignOff.Picture); // not approved yet
        Assert.Contains("only once approved", f.Note);
    }

    [Theory]
    [InlineData("Kind regards,")]
    [InlineData("Regards")]
    [InlineData("Met vriendelike groete")]
    [InlineData("yours sincerely")]
    public void Common_closings_are_recognised(string closing) => Assert.True(SignOffLayout.IsClosing(closing));

    [Fact]
    public void Text_after_a_closing_is_never_dropped()
    {
        var f = SignOffLayout.Fit(["Regards", "PS: please also send the signed copy of the agreement back to us by Friday."], null, Mine(), null, true);
        Assert.Null(f.SignOff);
        Assert.Equal(2, f.Paragraphs.Count);
    }

    [Fact]
    public async Task Nobody_else_sets_your_sign_off_and_a_name_is_needed_to_switch_it_on()
    {
        using var env = new Env();
        var s = new SignOffService(env.Stores, env.Clock);
        await Assert.ThrowsAsync<DomainException>(() => s.SaveAsync(env.Alice, true, "Yours sincerely,", " ", null));
        await Assert.ThrowsAsync<DomainException>(() => s.SetPictureAsync(env.Alice, new MemoryStream("not a picture"u8.ToArray())));
        Assert.False((await s.GetAsync(env.Bob)).On);
    }

    [Fact]
    public async Task An_approved_letter_ends_with_the_authors_sign_off_and_signature_picture_in_Word_and_PDF()
    {
        using var env = new Env();
        var s = new SignOffService(env.Stores, env.Clock);
        await s.SaveAsync(env.Alice, true, "Kind regards,", "Alice Author", "Senior Partner");
        await s.SetPictureAsync(env.Alice, new MemoryStream(Png()));
        env.Speech.Heard = "Dear Mr Smith comma new paragraph thank you for your letter full stop";
        var id = await env.Meeting.CreateRecordingAsync(env.Alice, env.Dictation());
        await env.Processing.ProcessAsync(id);
        var exports = new ExportService(env.Stores, env.Clock);

        // A draft: closing, name and title, but no signature picture yet.
        var draft = await exports.ExportAsync(env.Alice, id, "docx");
        using (var doc = WordprocessingDocument.Open(new MemoryStream(draft.Content), false))
        {
            var text = doc.MainDocumentPart!.Document!.Body!.InnerText;
            Assert.Contains("Kind regards,", text);
            Assert.EndsWith("Alice AuthorSenior Partner", text);
            Assert.Empty(doc.MainDocumentPart.ImageParts);
        }
        Assert.Contains("only once approved", (await env.Stores.Audit.ListAsync(id)).Last(e => e.Type == "item.exported").Details);

        await env.Meeting.ApproveAsync(env.Alice, id);
        var word = await exports.ExportAsync(env.Alice, id, "docx");
        using (var doc = WordprocessingDocument.Open(new MemoryStream(word.Content), false))
        {
            Assert.Single(doc.MainDocumentPart!.ImageParts);
            Assert.Empty(new DocumentFormat.OpenXml.Validation.OpenXmlValidator().Validate(doc));
        }
        var pdf = await exports.ExportAsync(env.Alice, id, "pdf");
        Assert.Contains("/Subtype/Image", System.Text.Encoding.Latin1.GetString(pdf.Content));
        Assert.Contains("with signature picture", (await env.Stores.Audit.ListAsync(id)).Last(e => e.Type == "item.exported").Details);
    }

    [Fact]
    public async Task Someone_elses_letter_uses_its_authors_sign_off_not_the_exporters()
    {
        using var env = new Env();
        await new SignOffService(env.Stores, env.Clock).SaveAsync(env.Carol, true, "Yours sincerely,", "Carol Admin", null);
        env.Speech.Heard = "Dear Sir full stop";
        var id = await env.Meeting.CreateRecordingAsync(env.Alice, env.Dictation());
        await env.Processing.ProcessAsync(id);
        var file = await new ExportService(env.Stores, env.Clock).ExportAsync(env.Carol, id, "docx");
        using var doc = WordprocessingDocument.Open(new MemoryStream(file.Content), false);
        Assert.DoesNotContain("Carol Admin", doc.MainDocumentPart!.Document!.Body!.InnerText);
        Assert.Contains("none (the author", (await env.Stores.Audit.ListAsync(id)).Last(e => e.Type == "item.exported").Details);
    }
}
