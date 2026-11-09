using VerbaFlow.Core.Domain;
using VerbaFlow.Core.Providers;
using VerbaFlow.Infrastructure.Export;
using VerbaFlow.Infrastructure.Services;

namespace VerbaFlow.Tests.Services;

public class SharedFontTests
{
    private sealed class Ar : ITranslator
    {
        public string Name => "fake";
        public Task<IReadOnlyList<TextPiece>> TranslateAsync(IReadOnlyList<TextPiece> pieces, string language, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<TextPiece>>(pieces.Select(p => new TextPiece(p.Id, "شكرا لك على رسالتك المؤرخة 12 مارس")).ToList());
    }

    // On Windows, Arial is both the text font and (with its built-up letters flattened) the Arabic font. PDFsharp keeps
    // fonts by name, so the two copies clashed and every Arabic PDF failed (owner, 2026-11-09). On Linux the same clash
    // happens with a Verdana template, whose look-alike is DejaVu Sans, also the Arabic font here.
    [Fact]
    public async Task Arabic_text_exports_when_the_same_font_file_is_also_the_text_font()
    {
        using var env = new Env();
        env.Speech.Heard = "We agreed the budget today";
        env.Ai.Reply = new AiOutputs("fake-ai", "en", "The budget was agreed.", ["Ann: send the budget"], "Decisions:\n- Budget agreed", "Calm");
        var id = await env.ProcessedRecordingAsync();
        var t = await new MinutesTemplateService(env.Stores, env.Clock).CreateAsync(env.Carol, MinutesTemplateTests.Input(font: "Verdana"));
        await new TranslationService(env.Stores, new Ar(), env.Clock, env.Reporter).TranslateAsync(env.Alice, id, "ar");
        var ex = new ExportService(env.Stores, env.Clock, null, env.Reporter, PdfSigner.Create(null, null, Path.Combine(env.Dir, "keys")));
        await ex.ExportAsync(env.Alice, id, "pdf", template: t.Id.ToString());
        var pdf = await ex.ExportAsync(env.Alice, id, "pdf", template: t.Id.ToString(), translation: "ar");
        SigningTests.VerifySignature(pdf.Content);
        File.WriteAllBytes(Path.Combine(Path.GetTempPath(), "vf-shared-font-ar.pdf"), pdf.Content);
    }
}
