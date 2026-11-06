using System.Text;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using VerbaFlow.Core.Domain;
using VerbaFlow.Core.Providers;
using VerbaFlow.Infrastructure.Export;
using VerbaFlow.Infrastructure.Services;

namespace VerbaFlow.Tests.Services;

public class ExportLanguageTests
{
    private static async Task<(Env Env, Guid Id, ExportService Exports)> MeetingIn(string language, string heard, AiOutputs outputs, bool approve = true)
    {
        var env = new Env();
        env.Speech.Heard = heard;
        env.Ai.Reply = outputs;
        var up = env.Recording() with { OutputLanguage = language, SpokenLanguages = language };
        var id = await env.Meeting.CreateRecordingAsync(env.Alice, up);
        await env.Processing.ProcessAsync(id);
        if (approve) await env.Meeting.ApproveAsync(env.Alice, id);
        return (env, id, new ExportService(env.Stores, env.Clock, null, env.Reporter, PdfSigner.Create(null, null, Path.Combine(env.Dir, "keys"))));
    }

    private static string WordText(byte[] docx)
    {
        using var doc = WordprocessingDocument.Open(new MemoryStream(docx), false);
        return string.Join("\n", doc.MainDocumentPart!.Document!.Body!.Descendants<Paragraph>().Select(p => p.InnerText));
    }

    /// <summary>The fonts embedded in a PDF (subset prefixes taken off).</summary>
    private static List<string> PdfFonts(byte[] pdf) => Regex.Matches(Encoding.Latin1.GetString(pdf), @"/BaseFont\s*/(?:[A-Z]{6}\+)?([^\s/\]>]+)")
        .Select(m => m.Groups[1].Value).Distinct().ToList();

    [Fact]
    public void Arabic_letters_are_joined_and_written_backwards_for_the_PDF()
    {
        // ل + ا becomes the lam-alef sign; م at the start of "مرحبا" takes its initial form.
        Assert.Equal("ﻻ", Scripts.Shape("لا"));
        var hello = Scripts.Shape("مرحبا");
        Assert.Equal('ﻣ', hello[0]); // initial meem
        Assert.Equal('ﺎ', hello[^1]); // final alef
        Assert.Equal("(ba", Scripts.Backwards("ab)"));
        Assert.True(Scripts.RightToLeft("123 مرحبا Bob"));
        Assert.False(Scripts.RightToLeft("Bob مرحبا"));

        // In a right-to-left line, Arabic words come from the right and a number keeps its digits in order.
        var order = Scripts.VisualOrder(["في", " ", "2024", " ", "اجتماع"], rightToLeft: true, out var backwards);
        Assert.Equal([4, 3, 2, 1, 0], order);
        Assert.False(backwards[2]);
        Assert.True(backwards[0]);
        // Latin words inside keep their order.
        order = Scripts.VisualOrder(["قال", " ", "Mr", " ", "Smith"], rightToLeft: true, out _);
        Assert.Equal([2, 3, 4, 1, 0], order);
    }

    [Fact]
    public void Every_language_has_all_its_words()
    {
        foreach (var code in ExportWords.Translated.Append("en").Append("mt").Append("zu"))
        {
            var w = ExportWords.For(code);
            foreach (var f in typeof(ExportWords).GetFields().Where(f => f.FieldType == typeof(string)))
                Assert.False(string.IsNullOrWhiteSpace((string?)f.GetValue(w)), $"{code}: {f.Name} is empty");
            Assert.Contains("{0}", w.ApprovedBy);
            Assert.Contains("{1}", w.ApprovedBy);
            Assert.False(string.IsNullOrEmpty(w.Date(new DateTimeOffset(2026, 3, 4, 10, 0, 0, TimeSpan.Zero))));
        }
        Assert.Equal("Résumé", ExportWords.For("fr-BE").Summary);
        Assert.Same(ExportWords.English, ExportWords.For("mt"));
        Assert.Contains("mars", ExportWords.For("fr").Date(new DateTimeOffset(2026, 3, 4, 10, 0, 0, TimeSpan.Zero)));
        Assert.Equal("2026年3月4日", ExportWords.For("zh").Date(new DateTimeOffset(2026, 3, 4, 10, 0, 0, TimeSpan.Zero)));
        // Arabic dates use the ordinary (Gregorian) calendar.
        Assert.Contains("2026", ExportWords.For("ar").Date(new DateTimeOffset(2026, 3, 4, 10, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public async Task A_French_meeting_exports_with_French_headings_labels_and_dates()
    {
        var (env, id, exports) = await MeetingIn("fr-FR", "Bonjour à tous merci d'être venus",
            new AiOutputs("fake-ai", "fr", "La réunion a porté sur le budget.", ["Ann : envoyer le budget"], "Décisions :\n- Budget accepté", "Calme"));
        using var _ = env;
        var word = WordText((await exports.ExportAsync(env.Alice, id, "docx")).Content);
        foreach (var expected in new[] { "APPROUVÉ", "Approuvé par Alice le", "Résumé", "Actions à mener", "Procès-verbal", "Ton de la réunion",
            "Transcription", "Enregistré le", "Durée", "Langues", "Exporté" })
            Assert.Contains(expected, word);
        Assert.DoesNotContain("Summary", word);
        Assert.Matches(@"\d+ \p{L}+ 2\d{3}", word); // a French month name

        // Only some parts: the Contains row lists them in French.
        var some = WordText((await exports.ExportAsync(env.Alice, id, "docx", parts: "summary,transcript")).Content);
        Assert.Contains("Contient", some);
        Assert.Contains("Résumé, Transcription", some);

        var pdf = await exports.ExportAsync(env.Alice, id, "pdf");
        SigningTests.VerifySignature(pdf.Content);
    }

    [Fact]
    public async Task A_template_heading_typed_by_an_admin_stays_and_a_default_one_is_translated()
    {
        var (env, id, exports) = await MeetingIn("de-DE", "Guten Morgen zusammen",
            new AiOutputs("fake-ai", "de", "Es ging um das Budget.", ["Ann: Budget senden"], "Beschlüsse:", "Ruhig"));
        using var _ = env;
        await new MinutesTemplateService(env.Stores, env.Clock).CreateAsync(env.Carol, MinutesTemplateTests.Input(isDefault: true,
            sections: [new("summary", "Summary", true), new("actions", "Next steps", true), new("minutes", "Minutes", true),
                new("tone", "Tone of the meeting", false), new("transcript", "Transcript", true)]));
        var word = WordText((await exports.ExportAsync(env.Alice, id, "docx")).Content);
        Assert.Contains("Zusammenfassung", word);
        Assert.Contains("Next steps", word); // typed by the admin
        Assert.Contains("Protokoll", word);
        Assert.Contains("Transkript", word);
        Assert.Contains("Genehmigt", word);
    }

    [Theory]
    [InlineData("zh-CN", "大家好 今天我们讨论预算", "会议讨论了预算。", "摘要")]
    [InlineData("ja-JP", "皆さんこんにちは 今日は予算について話します", "会議では予算について話し合いました。", "要約")]
    [InlineData("ar-AE", "مرحبا بالجميع اليوم نناقش الميزانية", "قال المدير إن الاجتماع ناقش الميزانية لعام 2026 (والعام القادم).", "الملخص")]
    public async Task Chinese_Japanese_and_Arabic_exports_use_fonts_that_have_their_letters(string language, string heard, string summary, string heading)
    {
        var (env, id, exports) = await MeetingIn(language, heard, new AiOutputs("fake-ai", language[..2], string.Concat(Enumerable.Repeat(summary + (language.StartsWith("ar") ? " " : ""), 8)), [summary], summary, summary));
        using var _ = env;
        var word = (await exports.ExportAsync(env.Alice, id, "docx")).Content;
        Assert.Contains(heading, WordText(word));
        if (language.StartsWith("ar"))
        {
            using var doc = WordprocessingDocument.Open(new MemoryStream(word), false);
            var para = doc.MainDocumentPart!.Document!.Body!.Descendants<Paragraph>().First(p => p.InnerText == heading);
            Assert.NotNull(para.ParagraphProperties?.BiDi); // Word lays it out right to left
        }

        var pdf = (await exports.ExportAsync(env.Alice, id, "pdf")).Content;
        SigningTests.VerifySignature(pdf);
        File.WriteAllBytes(Path.Combine(Path.GetTempPath(), $"vf-{language}.pdf"), pdf);
        var fonts = PdfFonts(pdf);
        // Besides the ordinary font, one with the script's letters is embedded (when the computer has one).
        Assert.True(fonts.Count >= 2, $"only {string.Join(", ", fonts)}");
    }

    [Fact]
    public async Task A_letter_dictated_in_Arabic_or_Spanish_gets_its_labels_and_date_in_that_language()
    {
        foreach (var (language, heard, expected) in new[]
        {
            ("es-ES", "Estimado señor García gracias por su carta", new[] { "N/Ref.:", "Borrador: aún no aprobado.", " de " }),
            ("ar-AE", "السيد العزيز شكرا على رسالتك", new[] { "مرجعنا:", "مسودة: لم تعتمد بعد." }),
        })
        {
            using var env = new Env();
            env.Speech.Heard = heard;
            var up = env.Dictation() with { OutputLanguage = language[..2], SpokenLanguages = language, ClientReference = "ABC-1" };
            var id = await env.Meeting.CreateRecordingAsync(env.Alice, up);
            await env.Processing.ProcessAsync(id);
            var exports = new ExportService(env.Stores, env.Clock, null, env.Reporter, PdfSigner.Create(null, null, Path.Combine(env.Dir, "keys")));
            var word = WordText((await exports.ExportAsync(env.Alice, id, "docx")).Content);
            foreach (var e in expected) Assert.Contains(e, word);
            await env.Meeting.ApproveAsync(env.Alice, id);
            var pdf = (await exports.ExportAsync(env.Alice, id, "pdf")).Content;
            SigningTests.VerifySignature(pdf);
            File.WriteAllBytes(Path.Combine(Path.GetTempPath(), $"vf-letter-{language}.pdf"), pdf);
        }
    }
}
