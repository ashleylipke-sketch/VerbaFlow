using System.Security.Cryptography;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using PdfSharp.Pdf.IO;
using VerbaFlow.Core.Audit;
using VerbaFlow.Core.Domain;

namespace VerbaFlow.Infrastructure.Services;

/// <summary>
/// The organisation's letterhead for dictated letters. Anyone signed in can see which letterhead is in use; only
/// administrators upload, replace or remove it. Each file is checked before it is accepted, kept under its SHA-256
/// fingerprint (never overwritten), and every change is written to the audit trail with that fingerprint.
/// </summary>
public sealed class LetterheadService(Stores stores, TimeProvider clock)
{
    public const long MaxBytes = 10 * 1024 * 1024;
    public const string Word = "word", Pdf = "pdf";
    public const int MinSpaceMm = 5, MaxSpaceMm = 120;

    public async Task<Letterhead> GetAsync() => await stores.Letterhead.GetAsync(Letterhead.SingleId) ?? Letterhead.Empty;

    /// <summary>The stored file for Word or PDF, or null when none is set (or its file is missing).</summary>
    public async Task<byte[]?> ReadAsync(string kind)
    {
        var l = await GetAsync();
        var f = kind == Word ? l.Word : l.Pdf;
        if (f is null) return null;
        var path = PathFor(f.Sha256, kind);
        return File.Exists(path) ? await File.ReadAllBytesAsync(path) : null;
    }

    public async Task<Letterhead> UploadAsync(User actor, string kind, Stream content, string? fileName)
    {
        RequireAdmin(actor);
        kind = Kind(kind);
        using var ms = new MemoryStream();
        await CopyLimitedAsync(content, ms);
        var bytes = ms.ToArray();
        if (bytes.Length == 0) throw new DomainException("The file is empty.");
        if (kind == Word) CheckWord(bytes); else CheckPdf(bytes);

        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var path = PathFor(sha, kind);
        Directory.CreateDirectory(stores.LetterheadRoot);
        if (!File.Exists(path)) await File.WriteAllBytesAsync(path, bytes); // same fingerprint means the same file is already kept

        var name = Path.GetFileName(fileName ?? "") is { Length: > 0 } n ? (n.Length > 120 ? n[..120] : n) : kind == Word ? "letterhead.docx" : "letterhead.pdf";
        var file = new LetterheadFile(sha, name, bytes.LongLength, actor.Id, clock.GetUtcNow());
        var current = await GetAsync();
        var previous = kind == Word ? current.Word : current.Pdf;
        var next = kind == Word ? current with { Word = file } : current with { Pdf = file };
        await stores.Letterhead.UpsertAsync(next.Id, next);
        await Audit(actor, "letterhead.uploaded", ("kind", kind), ("fileName", name), ("sha256", sha), ("previousSha256", previous?.Sha256), ("bytes", bytes.LongLength));
        return next;
    }

    public async Task<Letterhead> RemoveAsync(User actor, string kind)
    {
        RequireAdmin(actor);
        kind = Kind(kind);
        var current = await GetAsync();
        var previous = kind == Word ? current.Word : current.Pdf;
        if (previous is null) return current;
        var next = kind == Word ? current with { Word = null } : current with { Pdf = null };
        await stores.Letterhead.UpsertAsync(next.Id, next);
        await Audit(actor, "letterhead.removed", ("kind", kind), ("sha256", previous.Sha256));
        return next;
    }

    /// <summary>How far the letter's text keeps from the edges of a PDF letterhead page, in millimetres.</summary>
    public async Task<Letterhead> SetPdfSpaceAsync(User actor, int topFirst, int topNext, int bottom, int sides)
    {
        RequireAdmin(actor);
        foreach (var (v, what) in new[] { (topFirst, "top of the first page"), (topNext, "top of the pages after"), (bottom, "bottom"), (sides, "sides") })
            if (v < MinSpaceMm || v > MaxSpaceMm) throw new DomainException($"The space at the {what} must be between {MinSpaceMm} and {MaxSpaceMm} mm.");
        if (sides * 2 > 150) throw new DomainException("The spaces at the sides leave no room for the text. Make them smaller.");
        var current = await GetAsync();
        var next = current with { PdfTopFirstMm = topFirst, PdfTopNextMm = topNext, PdfBottomMm = bottom, PdfSideMm = sides };
        await stores.Letterhead.UpsertAsync(next.Id, next);
        await Audit(actor, "letterhead.spacing-changed", ("topFirstMm", topFirst), ("topNextMm", topNext), ("bottomMm", bottom), ("sidesMm", sides));
        return next;
    }

    /// <summary>A sample letter on the current letterhead, so an administrator can see how letters will look and adjust the spaces.</summary>
    public async Task<byte[]> SampleAsync(string format)
    {
        var l = await GetAsync();
        var letter = new Export.LetterDocument("Sample letter", clock.GetLocalNow().ToString("d MMMM yyyy", System.Globalization.CultureInfo.InvariantCulture),
            "SAMPLE-001", null,
        [
            "Mr J Smith", "1 Example Road", "Cape Town", "Dear Mr Smith,",
            "This is a sample letter. It shows where the text of a dictated letter will sit on your letterhead, so you can check that it stays clear of the logo, the address and anything printed at the foot of the page.",
            .. Enumerable.Repeat("This paragraph is here to fill the page, so you can also see where the text continues on the second page and how much room the letterhead leaves there.", 14),
            "Yours sincerely,", "Ashley Example",
        ]);
        return format == Word ? Export.LetterWriter.Word(letter, await ReadAsync(Word))
            : Export.LetterWriter.Pdf(letter, await ReadAsync(Pdf), new Export.LetterSpace(l.PdfTopFirstMm, l.PdfTopNextMm, l.PdfBottomMm, l.PdfSideMm));
    }

    private string PathFor(string sha, string kind) => Path.Combine(stores.LetterheadRoot, sha + (kind == Word ? ".docx" : ".pdf"));

    private static string Kind(string kind) => kind?.ToLowerInvariant() switch
    {
        "word" or "docx" => Word, "pdf" => Pdf, _ => throw new DomainException("Choose a Word or a PDF letterhead."),
    };

    private static async Task CopyLimitedAsync(Stream from, Stream to)
    {
        var buffer = new byte[81920];
        long total = 0;
        int n;
        while ((n = await from.ReadAsync(buffer)) > 0)
        {
            total += n;
            if (total > MaxBytes) throw new DomainException("The file is too big. A letterhead can be at most 10 MB.");
            await to.WriteAsync(buffer.AsMemory(0, n));
        }
    }

    /// <summary>A Word document or template (.docx or .dotx). Files with macros (.docm, .dotm) are refused.</summary>
    private static void CheckWord(byte[] bytes)
    {
        try
        {
            using var doc = WordprocessingDocument.Open(new MemoryStream(bytes), false);
            if (doc.DocumentType is not (WordprocessingDocumentType.Document or WordprocessingDocumentType.Template))
                throw new DomainException("Word files with macros cannot be used. Save the letterhead as a .docx file.");
            if (doc.MainDocumentPart?.Document?.Body is null) throw new DomainException("This Word file has no page in it.");
        }
        catch (DomainException) { throw; }
        catch (Exception) { throw new DomainException("This is not a Word file VerbaFlow can read. Save the letterhead as a .docx file and try again."); }
    }

    private static void CheckPdf(byte[] bytes)
    {
        try
        {
            using var pdf = PdfReader.Open(new MemoryStream(bytes), PdfDocumentOpenMode.Import);
            if (pdf.PageCount == 0) throw new DomainException("This PDF has no pages.");
        }
        catch (DomainException) { throw; }
        catch (Exception) { throw new DomainException("This PDF cannot be used. It may be password protected or damaged. Save it again without a password and try again."); }
    }

    private static void RequireAdmin(User actor)
    {
        if (!actor.IsAdmin) throw new ForbiddenException("Only administrators can change the letterhead.");
    }

    private Task Audit(User actor, string action, params (string, object?)[] details) =>
        stores.Audit.AppendAsync(actor.Id, "admin", "platform", null, action, AuditChain.Details(details));
}
