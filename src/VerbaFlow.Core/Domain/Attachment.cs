namespace VerbaFlow.Core.Domain;

/// <summary>
/// A file kept with a meeting or dictation (an agenda, papers, slides, a letter being answered). It belongs to the whole
/// recording (all its versions): a reopened version keeps it. The file is stored write-once under <see cref="Id"/>.
/// </summary>
public sealed record Attachment(Guid Id, Guid ChainId, Guid AddedOnItemId, string FileName, string ContentType, long SizeBytes,
    string Sha256, Guid AddedBy, DateTimeOffset AddedAt)
{
    public const long MaxBytes = 25L * 1024 * 1024;
    public const int MaxPerItem = 20;

    /// <summary>File types that can be attached, with the type they are served as. Files with macros and programs are refused.</summary>
    public static readonly IReadOnlyDictionary<string, string> Types = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"] = "application/pdf",
        [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        [".pptx"] = "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        [".doc"] = "application/msword",
        [".xls"] = "application/vnd.ms-excel",
        [".ppt"] = "application/vnd.ms-powerpoint",
        [".odt"] = "application/vnd.oasis.opendocument.text",
        [".ods"] = "application/vnd.oasis.opendocument.spreadsheet",
        [".odp"] = "application/vnd.oasis.opendocument.presentation",
        [".rtf"] = "application/rtf",
        [".txt"] = "text/plain",
        [".csv"] = "text/csv",
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".msg"] = "application/vnd.ms-outlook",
        [".eml"] = "message/rfc822",
    };

    /// <summary>The type to store and serve a file as, or a plain sentence saying why it cannot be attached.</summary>
    public static string TypeFor(string fileName)
    {
        var ext = Path.GetExtension(fileName ?? "");
        if (Types.TryGetValue(ext, out var t)) return t;
        if (ext.ToLowerInvariant() is ".docm" or ".xlsm" or ".pptm" or ".dotm" or ".xlsb")
            throw new DomainException($"\"{fileName}\" contains macros, which could run code, so it cannot be attached. Save it without macros (for example as .docx or .pdf) and try again.");
        throw new DomainException($"\"{fileName}\" cannot be attached. Attach PDF, Word, Excel, PowerPoint, text, picture or email files.");
    }

    /// <summary>Whether the first bytes look like the file type its name says (so a program renamed to .pdf is refused).</summary>
    public static bool LooksLike(string fileName, ReadOnlySpan<byte> head)
    {
        static bool Starts(ReadOnlySpan<byte> h, params byte[] sig) => h.Length >= sig.Length && h[..sig.Length].SequenceEqual(sig);
        if (Starts(head, 0x4D, 0x5A)) return false; // a Windows program, whatever its name
        return Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".pdf" => Starts(head, (byte)'%', (byte)'P', (byte)'D', (byte)'F'),
            ".docx" or ".xlsx" or ".pptx" or ".odt" or ".ods" or ".odp" => Starts(head, (byte)'P', (byte)'K', 3, 4),
            ".doc" or ".xls" or ".ppt" or ".msg" => Starts(head, 0xD0, 0xCF, 0x11, 0xE0),
            ".png" => Starts(head, 0x89, (byte)'P', (byte)'N', (byte)'G'),
            ".jpg" or ".jpeg" => Starts(head, 0xFF, 0xD8, 0xFF),
            ".gif" => Starts(head, (byte)'G', (byte)'I', (byte)'F', (byte)'8'),
            ".webp" => Starts(head, (byte)'R', (byte)'I', (byte)'F', (byte)'F'),
            ".rtf" => Starts(head, (byte)'{', (byte)'\\', (byte)'r', (byte)'t', (byte)'f'),
            _ => true, // plain text, CSV and email files have no fixed start
        };
    }
}
