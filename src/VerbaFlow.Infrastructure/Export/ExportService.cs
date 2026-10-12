using VerbaFlow.Core.Transcripts;
using System.Globalization;
using System.Security.Cryptography;
using VerbaFlow.Core.Audit;
using VerbaFlow.Core.Domain;
using VerbaFlow.Core.Providers;
using VerbaFlow.Core.Security;
using VerbaFlow.Infrastructure.Services;

namespace VerbaFlow.Infrastructure.Export;

/// <summary>One file that was exported: its fingerprint, so a copy can later be checked for changes.</summary>
public sealed record ExportRecord(Guid Id, Guid ItemId, int VersionNo, int TranscriptVersionNo, string Format, string Marker,
    string Sha256, long SizeBytes, Guid ExportedBy, DateTimeOffset ExportedAt, string? Parts = null);

public sealed record ExportFile(byte[] Content, string ContentType, string FileName);

/// <summary>The answer to "is this the file VerbaFlow exported?", in plain words.</summary>
public sealed record ExportCheck(bool Matches, string Message, string Sha256, DateTimeOffset? ExportedAt = null, string? ExportedBy = null,
    string? Marker = null, string? Format = null);

/// <summary>
/// Exports a meeting (details, summary, action points, minutes, tone and transcript) as Word or PDF. Every export is
/// marked Approved, Draft or Superseded, its SHA-256 fingerprint is stored and written to the audit trail, and a file can
/// be checked later against those fingerprints: any change to the file, even one character, gives a different fingerprint.
/// Digital signing with the organisation's certificate (decision 13) is not built yet.
/// </summary>
public sealed class ExportService(Stores stores, TimeProvider clock, IEmailSender? email = null, FailureReporter? reporter = null)
{
    /// <summary>The most people one email can go to.</summary>
    public const int MaxRecipients = 10;

    /// <param name="purpose">"download" or "preview"; written to the audit trail.</param>
    /// <param name="parts">Which parts to include (see <see cref="ExportParts"/>); null means everything.</param>
    public async Task<ExportFile> ExportAsync(User actor, Guid id, string format, string purpose = "download", string? parts = null) =>
        (await CreateAsync(actor, id, format, purpose, ExportParts.Parse(parts))).File;

    /// <summary>
    /// Emails the exported file as an attachment. The addresses are checked first; nothing is sent if one is not a valid
    /// address. The recipients, the file's fingerprint and the mail service's reference go into the audit trail.
    /// </summary>
    public async Task<string> EmailAsync(User actor, Guid id, string format, string recipients, string? note, CancellationToken ct = default,
        string? parts = null)
    {
        if (email is null) throw new DomainException("Email is not available.");
        var to = ParseRecipients(recipients);
        var chosen = ExportParts.Parse(parts);
        if (note is { Length: > 2000 }) throw new DomainException("The message is too long. Keep it under 2,000 characters.");
        var (file, record, item) = await CreateAsync(actor, id, format, "email", chosen);
        var text = $"Please find attached {What(item)} \"{item.Name}\" ({record.Marker.ToLowerInvariant()}).\r\n"
            + $"Sent by {actor.Name} using VerbaFlow.\r\n"
            + (string.IsNullOrWhiteSpace(note) ? "" : $"\r\nMessage from {actor.Name}:\r\n{note.Trim()}\r\n")
            + "\r\nTo confirm this file has not been changed, the sender can check it with \"Check an exported file\" in VerbaFlow.\r\n";
        var subject = Subject(item, record);
        string reference;
        try
        {
            reference = await email.SendAsync(new EmailMessage(to, subject, text, [new EmailAttachment(file.FileName, file.ContentType, file.Content)],
                string.IsNullOrWhiteSpace(actor.Email) ? null : actor.Email), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && reporter is not null)
        {
            // The person sees a plain sentence and a reference; the technical detail goes to support only.
            var failure = await reporter.ReportAsync("email", id, ex);
            await stores.Audit.AppendAsync(actor.Id, Capacity(actor, item), "speak", id, "email.failed",
                AuditChain.Details(("to", string.Join(", ", to)), ("kind", failure.Kind.ToString()), ("reference", failure.Reference)));
            throw new DomainException(failure.UserMessage);
        }
        await stores.Audit.AppendAsync(actor.Id, Capacity(actor, item), "speak", id, "item.emailed",
            AuditChain.Details(("to", string.Join(", ", to)), ("format", record.Format), ("marker", record.Marker), ("sha256", record.Sha256),
                ("withMessage", !string.IsNullOrWhiteSpace(note)), ("mailService", email.Name), ("mailReference", reference)));
        return $"Sent to {string.Join(", ", to)}.";
    }

    /// <summary>
    /// A new, unsent email with the exported file attached, for the person to open in their own email program (Outlook
    /// opens it as a draft from their account). They choose the addressees there, so VerbaFlow does not know who it goes to;
    /// the file's fingerprint is still recorded, so a recipient's copy can be checked.
    /// </summary>
    public async Task<ExportFile> DraftAsync(User actor, Guid id, string format, string? parts = null)
    {
        var (file, record, item) = await CreateAsync(actor, id, format, "email-draft", ExportParts.Parse(parts));
        var text = $"Please find attached {What(item)} \"{item.Name}\" ({record.Marker.ToLowerInvariant()}).\r\n"
            + "\r\nTo confirm the file has not been changed, it can be checked with \"Check an exported file\" in VerbaFlow.\r\n";
        var eml = EmlWriter.Write(new EmailMessage([], Subject(item, record), text, [new EmailAttachment(file.FileName, file.ContentType, file.Content)]), draft: true);
        return new ExportFile(eml, "message/rfc822", Path.ChangeExtension(file.FileName, ".eml"));
    }

    /// <summary>The heading over a dictation's text: what the person said it was for.</summary>
    public static string DocumentHeading(string? type) => type switch { "letter" => "Letter", "document" => "Document", "email" => "Email", _ => "Text" };

    private static string What(Item item) => item.Mode == ItemMode.Dictate ? "the dictation" : "the minutes of";

    private static string Subject(Item item, ExportRecord record) =>
        $"{(item.Mode == ItemMode.Dictate ? "Dictation" : "Meeting minutes")}: {item.Name}" + (record.Marker == "Approved" ? "" : $" ({record.Marker.ToUpperInvariant()})");

    /// <summary>Addresses separated by commas, semicolons, spaces or new lines. Each must be a plain, valid email address.</summary>
    public static IReadOnlyList<string> ParseRecipients(string? text)
    {
        var parts = (text ?? "").Split([',', ';', ' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (parts.Count == 0) throw new DomainException("Type at least one email address.");
        if (parts.Count > MaxRecipients) throw new DomainException($"Send to at most {MaxRecipients} people at a time.");
        foreach (var p in parts)
            if (!System.Net.Mail.MailAddress.TryCreate(p, out var a) || a.Address != p || !p.Contains('.', StringComparison.Ordinal) || p.Length > 254)
                throw new DomainException($"\"{(p.Length > 60 ? p[..60] + "…" : p)}\" is not a valid email address.");
        return parts;
    }

    private async Task<(ExportFile File, ExportRecord Record, Item Item)> CreateAsync(User actor, Guid id, string format, string purpose,
        IReadOnlyList<string> parts)
    {
        format = format.ToLowerInvariant() switch { "docx" or "word" => ExportFormats.Word, "pdf" => ExportFormats.Pdf,
            _ => throw new DomainException("Choose Word or PDF.") };
        var item = await stores.RequireItemAsync(id);
        PermissionEvaluator.Require(actor, item, Capability.Export);
        // A dictation is just its text: there is no summary, minutes or tone, and no speakers or times to show.
        var dictation = item.Mode == ItemMode.Dictate;
        if (dictation) parts = [ExportParts.Transcript];
        if (item.Status == ItemStatus.Purged) throw new NotFoundException("This recording has been deleted.");
        var transcript = await stores.Transcripts.GetAsync(id) ?? throw new DomainException("There is no transcript to export yet.");
        var outputs = (await stores.Outputs.GetAsync(id))?.Outputs;
        var all = await stores.Items.ListAsync();
        var users = (await stores.Users.ListAsync()).ToDictionary(u => u.Id, u => u.Name);
        var superseded = all.Any(i => i.SupersedesItemId == id);
        var now = clock.GetUtcNow();

        var marker = superseded ? "Superseded" : item.Status == ItemStatus.Completed ? "Approved" : "Draft";
        var markerLine = marker switch
        {
            "Approved" => $"Approved by {Name(users, item.ApprovedBy)} on {When(item.ApprovedAt!.Value)}.",
            "Superseded" => "A newer version of this meeting exists. This copy is kept for the record only.",
            _ => "Not approved yet. The text may still change.",
        };
        var ownerLabel = item.Source == SourceKind.Imported ? "Imported by" : "Recorded by";
        var details = new List<(string, string)>
        {
            (item.Source == SourceKind.Imported ? "Imported on" : "Recorded on", When(item.CreatedAt)),
            ("Length", Length(item.LengthMs)),
            (ownerLabel, Name(users, item.OwnerId)),
        };
        if (!string.IsNullOrWhiteSpace(item.ClientReference)) details.Add(("Client reference", item.ClientReference));
        if (!string.IsNullOrWhiteSpace(item.Description)) details.Add(("Description", item.Description));
        details.Add(("Languages", Languages(item.SpokenLanguages)));
        details.Add(("Version", $"{item.VersionNo} (transcript version {transcript.Current.No})"));
        details.Add(("Exported", $"{When(now)} by {actor.Name}"));
        var everything = parts.Count == ExportParts.All.Count;
        if (!everything && !dictation) details.Add(("Contains", string.Join(", ", parts.Select(ExportParts.NameOf))));
        bool Has(string part) => parts.Contains(part);

        var doc = new MinutesDocument(
            item.Name, marker, markerLine, details,
            Has(ExportParts.Summary) ? outputs?.Summary : null,
            Has(ExportParts.Actions) ? outputs?.ActionPoints ?? [] : [],
            Has(ExportParts.Minutes) ? (outputs?.Minutes ?? "").Split('\n').Select(l => l.TrimEnd()).Where(l => l.Length > 0).ToList() : [],
            Has(ExportParts.Tone) ? outputs?.ToneOfMeeting : null,
            Has(ExportParts.Transcript)
                ? dictation
                    ? DictationCommands.Paragraphs(transcript.Render(), r => r.Segment)
                        // A line break typed while editing is a paragraph break too.
                        .SelectMany(p => string.Join(" ", p.Select(r => r.Segment.Text)).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        .Select(text => new MinutesPassage("", "", text)).ToList()
                    : transcript.Render().Where(r => r.Segment.Text.Length > 0).Select(r => new MinutesPassage(Clock(r.Segment.StartMs), r.SpeakerName, r.Segment.Text)).ToList()
                : [],
            $"VerbaFlow · {item.Name} · {marker} · reference {item.Id.ToString("N")[..8].ToUpperInvariant()}",
            dictation ? DocumentHeading(item.DocumentType) : "Transcript");

        byte[] bytes;
        if (dictation && item.DocumentType == "letter")
        {
            // A dictated letter goes out as a letter, on the organisation's letterhead when one is set.
            var letterDate = item.ApprovedAt ?? item.CreatedAt;
            var letter = new LetterDocument(item.Name,
                TimeZoneInfo.ConvertTime(letterDate, TimeZoneInfo.Local).ToString("d MMMM yyyy", CultureInfo.InvariantCulture),
                string.IsNullOrWhiteSpace(item.ClientReference) ? null : item.ClientReference.Trim(),
                marker switch { "Approved" => null, "Superseded" => "Superseded: a newer version of this letter exists.", _ => "Draft: not approved yet." },
                doc.Transcript.Select(t => t.Text).ToList());
            var letterheads = new Services.LetterheadService(stores, clock);
            var l = await letterheads.GetAsync();
            bytes = format == ExportFormats.Word
                ? LetterWriter.Word(letter, await letterheads.ReadAsync(Services.LetterheadService.Word))
                : LetterWriter.Pdf(letter, await letterheads.ReadAsync(Services.LetterheadService.Pdf),
                    new LetterSpace(l.PdfTopFirstMm, l.PdfTopNextMm, l.PdfBottomMm, l.PdfSideMm));
        }
        else bytes = format == ExportFormats.Word ? DocxMinutesWriter.Write(doc) : PdfMinutesWriter.Write(doc);
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var record = new ExportRecord(Guid.NewGuid(), id, item.VersionNo, transcript.Current.No, format, marker, sha, bytes.LongLength, actor.Id, now,
            string.Join(",", parts));
        await stores.Exports.UpsertAsync(record.Id, record);
        await stores.Audit.AppendAsync(actor.Id, Capacity(actor, item), "speak", id, "item.exported",
            AuditChain.Details(("format", format), ("marker", marker), ("purpose", purpose), ("parts", string.Join(",", parts)), ("transcriptVersion", transcript.Current.No),
                ("sha256", sha), ("bytes", bytes.LongLength)));

        var safeName = new string(item.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c is '"' ? '_' : c).ToArray()).Trim();
        if (safeName.Length == 0) safeName = "meeting";
        if (safeName.Length > 80) safeName = safeName[..80];
        return (new ExportFile(bytes, ExportFormats.ContentType(format), $"{safeName} ({marker.ToLowerInvariant()}).{format}"), record, item);
    }

    /// <summary>Checks whether a file is exactly one that was exported from this meeting.</summary>
    public async Task<ExportCheck> CheckAsync(User actor, Guid id, Stream file)
    {
        var item = await stores.RequireItemAsync(id);
        PermissionEvaluator.Require(actor, item, Capability.Read);
        var sha = Convert.ToHexString(await SHA256.HashDataAsync(file)).ToLowerInvariant();
        var chain = (await stores.Items.ListAsync()).Where(i => i.ChainId == item.ChainId).Select(i => i.Id).ToHashSet();
        var match = (await stores.Exports.ListAsync()).Where(e => chain.Contains(e.ItemId) && e.Sha256 == sha)
            .OrderBy(e => e.ExportedAt).FirstOrDefault();
        await stores.Audit.AppendAsync(actor.Id, Capacity(actor, item), "speak", id, "export.checked",
            AuditChain.Details(("sha256", sha), ("matches", match is not null)));
        if (match is null)
            return new ExportCheck(false, "This file does not match any file exported from this meeting. It may have been changed, or it comes from a different meeting.", sha);
        var users = (await stores.Users.ListAsync()).ToDictionary(u => u.Id, u => u.Name);
        var by = Name(users, match.ExportedBy);
        var version = (await stores.Items.GetAsync(match.ItemId))?.VersionNo;
        return new ExportCheck(true,
            $"This file is unchanged. It is the {(match.Format == ExportFormats.Word ? "Word" : "PDF")} file exported by {by} on {When(match.ExportedAt)} ({match.Marker.ToLowerInvariant()}, version {version}).",
            sha, match.ExportedAt, by, match.Marker, match.Format);
    }

    private static string Name(Dictionary<Guid, string> users, Guid? id) => id is { } x ? users.GetValueOrDefault(x, "someone") : "someone";

    private static string Capacity(User actor, Item item) => PermissionEvaluator.RoleOn(actor, item) switch
    {
        ItemRole.Owner => item.Source == SourceKind.Imported ? "importer" : "author",
        ItemRole.Assignee => "assignee",
        _ => actor.IsAdmin ? "admin" : "user"
    };

    /// <summary>Dates in the computer's own time zone, with the offset so the time is never ambiguous.</summary>
    private static string When(DateTimeOffset t)
    {
        var local = TimeZoneInfo.ConvertTime(t, TimeZoneInfo.Local);
        var offset = local.Offset;
        var zone = offset == TimeSpan.Zero ? "UTC" : $"UTC{(offset < TimeSpan.Zero ? "-" : "+")}{offset:hh\\:mm}";
        return local.ToString("d MMMM yyyy, HH:mm", CultureInfo.InvariantCulture) + " " + zone;
    }

    private static string Length(int ms) => ms <= 0 ? "unknown" : TimeSpan.FromMilliseconds(ms) is var t && t.TotalHours >= 1
        ? $"{(int)t.TotalHours} h {t.Minutes} min" : $"{t.Minutes} min {t.Seconds} s";

    private static string Clock(int ms) => TimeSpan.FromMilliseconds(ms) is var t && t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

    private static string Languages(string codes) => codes == "auto" ? "Detected automatically"
        : string.Join(", ", codes.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(c => SpokenLanguageCatalogue.NameOf(c.Trim())));
}
