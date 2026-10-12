namespace VerbaFlow.Core.Domain;

/// <summary>One uploaded letterhead file. The file itself is kept under its fingerprint and never changed.</summary>
public sealed record LetterheadFile(string Sha256, string FileName, long SizeBytes, Guid UploadedBy, DateTimeOffset UploadedAt);

/// <summary>
/// The organisation's letterhead for dictated letters: a Word file (its headers, footers, page size, margins and fonts
/// are kept, and the letter goes in its body) and a PDF (page 1 behind the first page of a letter, page 2, if there is
/// one, behind the pages after). The spaces say how far from the edges of a PDF page the letter's text may go, so it
/// stays clear of the logo and address printed on it.
/// </summary>
public sealed record Letterhead(Guid Id, LetterheadFile? Word, LetterheadFile? Pdf,
    int PdfTopFirstMm = Letterhead.DefaultTopFirstMm, int PdfTopNextMm = Letterhead.DefaultTopNextMm,
    int PdfBottomMm = Letterhead.DefaultBottomMm, int PdfSideMm = Letterhead.DefaultSideMm)
{
    public static readonly Guid SingleId = new("00000000-0000-0000-0000-00000000a001");
    public const int DefaultTopFirstMm = 55, DefaultTopNextMm = 25, DefaultBottomMm = 30, DefaultSideMm = 25;
    public static Letterhead Empty => new(SingleId, null, null);
}
