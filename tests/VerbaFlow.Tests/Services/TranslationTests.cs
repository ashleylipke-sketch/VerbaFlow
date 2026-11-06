using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using VerbaFlow.Core.Domain;
using VerbaFlow.Core.Providers;
using VerbaFlow.Infrastructure.Azure;
using VerbaFlow.Infrastructure.Export;
using VerbaFlow.Infrastructure.Services;
using VerbaFlow.Infrastructure.StandIns;

namespace VerbaFlow.Tests.Services;

public class TranslationTests
{
    private sealed class FakeTranslator : ITranslator
    {
        public Func<TextPiece, string?> Translate { get; set; } = p => "[fr] " + p.Text;
        public bool Fail { get; set; }
        public IReadOnlyList<TextPiece>? Last { get; private set; }
        public string Name => "fake-translation";
        public Task<IReadOnlyList<TextPiece>> TranslateAsync(IReadOnlyList<TextPiece> pieces, string language, CancellationToken ct)
        {
            Last = pieces;
            if (Fail) throw new InvalidOperationException("translation service down");
            return Task.FromResult<IReadOnlyList<TextPiece>>(pieces.Select(p => (p, t: Translate(p))).Where(x => x.t is not null)
                .Select(x => new TextPiece(x.p.Id, x.t!)).ToList());
        }
    }

    private static ExportService Exports(Env env) =>
        new(env.Stores, env.Clock, null, env.Reporter, PdfSigner.Create(null, null, Path.Combine(env.Dir, "keys")));

    private static string WordText(byte[] docx)
    {
        using var doc = WordprocessingDocument.Open(new MemoryStream(docx), false);
        return string.Join("\n", doc.MainDocumentPart!.Document!.Body!.Descendants<Paragraph>().Select(p => p.InnerText));
    }

    [Fact]
    public async Task A_meeting_is_translated_beside_the_original_and_exports_in_that_language()
    {
        using var env = new Env();
        env.Speech.Heard = "We agreed the budget today";
        env.Ai.Reply = new AiOutputs("fake-ai", "en", "The budget was agreed.", ["Ann: send the budget"], "Decisions:\n- Budget agreed", "Calm");
        var id = await env.ProcessedRecordingAsync();
        var fake = new FakeTranslator();
        var tr = new TranslationService(env.Stores, fake, env.Clock, env.Reporter);

        var list = await tr.ListAsync(env.Alice, id);
        Assert.True(list.CanTranslate);
        Assert.DoesNotContain(list.Languages, l => l.Code == "en"); // already in English
        await Assert.ThrowsAsync<ForbiddenException>(() => tr.TranslateAsync(env.Xavier, id, "fr"));
        await Assert.ThrowsAsync<DomainException>(() => tr.TranslateAsync(env.Alice, id, "en"));
        await Assert.ThrowsAsync<DomainException>(() => tr.TranslateAsync(env.Alice, id, "xh"));

        var row = await tr.TranslateAsync(env.Alice, id, "fr-FR");
        Assert.Equal("fr", row.Language);
        Assert.True(row.UpToDate);
        Assert.Contains(fake.Last!, p => p.Id == "summary");
        Assert.All(fake.Last!, p => Assert.False(Guid.TryParse(p.Id, out _))); // passage ids are never sent

        var page = await tr.GetAsync(env.Alice, id, "fr");
        Assert.Equal("[fr] The budget was agreed.", page.SummaryTranslated);
        Assert.Equal("The budget was agreed.", page.Summary);
        Assert.Equal(["[fr] Ann: send the budget"], page.ActionsTranslated);
        var line = Assert.Single(page.Lines);
        Assert.Equal("[fr] " + line.Original, line.Translated);
        // The recording itself is unchanged.
        Assert.Equal(line.Original, (await env.Stores.Transcripts.GetAsync(id))!.Render().Single().Segment.Text);

        var file = await Exports(env).ExportAsync(env.Alice, id, "docx", translation: "fr");
        var word = WordText(file.Content);
        Assert.Contains("[fr] The budget was agreed.", word);
        Assert.Contains("[fr] " + line.Original, word);
        Assert.Contains("Résumé", word);
        Assert.Contains("Traduction automatique", word);
        Assert.Contains(", fr).docx", file.FileName);
        var pdf = await Exports(env).ExportAsync(env.Alice, id, "pdf", translation: "fr");
        SigningTests.VerifySignature(pdf.Content);
        var history = (await env.Stores.Audit.ListAsync(id)).ToList();
        Assert.Contains(history, a => a.Type == "translation.made");
        Assert.Contains(history, a => a.Type == "item.exported" && a.Details.Contains("\"translation\":\"fr\""));
        Assert.DoesNotContain(history, a => a.Details.Contains("[fr]")); // the words stay out of the History

        // An edit makes it out of date: it cannot be exported until it is made again.
        var segId = (await env.Stores.Transcripts.GetAsync(id))!.Render().Single().Segment.Id;
        await env.Meeting.EditSegmentAsync(env.Alice, id, segId, "We agreed the budget this morning");
        Assert.False((await tr.ListAsync(env.Alice, id)).Translations.Single().UpToDate);
        var refused = await Assert.ThrowsAsync<DomainException>(() => Exports(env).ExportAsync(env.Alice, id, "docx", translation: "fr"));
        Assert.Contains("Translate it again", refused.Message);
        await Assert.ThrowsAsync<DomainException>(() => Exports(env).ExportAsync(env.Alice, id, "docx", translation: "de"));
        await tr.TranslateAsync(env.Alice, id, "fr");
        var again = (await tr.ListAsync(env.Alice, id)).Translations.Single();
        Assert.True(again.UpToDate);
        Assert.Contains("[fr] We agreed the budget this morning", WordText((await Exports(env).ExportAsync(env.Alice, id, "docx", translation: "fr")).Content));

        await tr.RemoveAsync(env.Alice, id, "fr");
        Assert.Empty((await tr.ListAsync(env.Alice, id)).Translations);

        // Deleting the recording deletes its translations.
        await tr.TranslateAsync(env.Alice, id, "de");
        await env.Meeting.DeleteAsync(env.Alice, id, "test");
        Assert.Empty(await env.Stores.Translations.ListAsync());
    }

    [Fact]
    public async Task A_translated_letter_has_translated_words_and_its_sign_off_closing_translated()
    {
        using var env = new Env();
        await new SignOffService(env.Stores, env.Clock).SaveAsync(env.Alice, true, "Yours sincerely,", "Alice Smith", null);
        env.Speech.Heard = "Thank you for your letter full stop";
        var id = await env.Meeting.CreateRecordingAsync(env.Alice, env.Dictation());
        await env.Processing.ProcessAsync(id);
        var fake = new FakeTranslator { Translate = p => p.Text == "Yours sincerely," ? "Cordialement," : "Merci pour votre lettre." };
        var tr = new TranslationService(env.Stores, fake, env.Clock, env.Reporter);
        await tr.TranslateAsync(env.Alice, id, "fr");
        var page = await tr.GetAsync(env.Alice, id, "fr");
        Assert.True(page.Dictation);
        Assert.Equal("Merci pour votre lettre.", Assert.Single(page.Lines).Translated);

        var word = WordText((await Exports(env).ExportAsync(env.Alice, id, "docx", translation: "fr")).Content);
        Assert.Contains("Merci pour votre lettre.", word);
        Assert.Contains("Cordialement,", word);
        Assert.DoesNotContain("Yours sincerely", word);
        Assert.Contains("Traduction automatique", word);
        Assert.Contains("Brouillon", word);
    }

    [Fact]
    public async Task A_failed_translation_gives_a_plain_sentence_and_a_reference()
    {
        using var env = new Env();
        var id = await env.ProcessedRecordingAsync();
        var tr = new TranslationService(env.Stores, new FakeTranslator { Fail = true }, env.Clock, env.Reporter);
        var ex = await Assert.ThrowsAsync<DomainException>(() => tr.TranslateAsync(env.Alice, id, "fr"));
        Assert.Contains("VF-", ex.Message);
        Assert.DoesNotContain("down", ex.Message);
        Assert.Contains((await env.Stores.Audit.ListAsync(id)), a => a.Type == "translation.failed");
        Assert.Empty(await env.Stores.Translations.ListAsync());

        // Pieces the translator leaves out stay as spoken, and are counted.
        var tr2 = new TranslationService(env.Stores, new FakeTranslator { Translate = _ => null }, env.Clock, env.Reporter);
        var row = await tr2.TranslateAsync(env.Alice, id, "fr");
        Assert.True(row.Untranslated > 0);
    }

    [Fact]
    public async Task The_stand_in_and_the_Azure_answer_are_read_as_expected()
    {
        var done = await new StandInTranslator().TranslateAsync([new("p1", "Hello\n- there")], "de", default);
        Assert.Equal("[de] Hello\n[de] - there", done.Single().Text);
        var parsed = AzureOpenAiTranslator.Parse("""{"pieces":[{"id":"p1","text":" Bonjour "}]}""");
        Assert.Equal(new TextPiece("p1", "Bonjour"), parsed.Single());
        Assert.Throws<ProviderException>(() => AzureOpenAiTranslator.Parse("{}"));
        Assert.Contains("untrusted", AzureOpenAiTranslator.SystemPrompt("fr"));
        Assert.Contains("French", AzureOpenAiTranslator.SystemPrompt("fr"));
    }
}
