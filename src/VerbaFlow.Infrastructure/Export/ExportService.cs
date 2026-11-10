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
    string Sha256, long SizeBytes, Guid ExportedBy, DateTimeOffset ExportedAt, string? Parts = null, string? Signature = null, string? Template = null, string? Translation = null);

public sealed record ExportFile(byte[] Content, string ContentType, string FileName);

/// <summary>The answer to "is this the file VerbaFlow exported?", in plain words.</summary>
public sealed record ExportCheck(bool Matches, string Message, string Sha256, DateTimeOffset? ExportedAt = null, string? ExportedBy = null,
    string? Marker = null, string? Format = null);

/// <summary>
/// Exports a meeting (details, summary, action points, minutes, tone and transcript) as Word or PDF. Every export is
/// marked Approved, Draft or Superseded, its SHA-256 fingerprint is stored and written to the audit trail, and a file can
/// be checked later against those fingerprints: any change to the file, even one character, gives a different fingerprint.
/// PDFs are digitally signed when a <see cref="PdfSigner"/> is set up (decision 13): with the organisation's certificate,
/// or a test certificate made on this computer until there is one. The fingerprint is taken after signing.
/// </summary>
public sealed class ExportService(Stores stores, TimeProvider clock, IEmailSender? email = null, FailureReporter? reporter = null,
    PdfSigner? signer = null, IMailDrafts? drafts = null, IOwnMailDrafts? ownDrafts = null)
{
    /// <summary>The most people one email can go to.</summary>
    public const int MaxRecipients = 10;

    /// <param name="purpose">"download" or "preview"; written to the audit trail.</param>
    /// <param name="parts">Which parts to include (see <see cref="ExportParts"/>); null means the template's usual parts.</param>
    /// <param name="template">A minutes template's id; null means the default template, "standard" VerbaFlow's own layout.</param>
    /// <param name="translation">A language code: export that machine translation instead of the original text.</param>
    public async Task<ExportFile> ExportAsync(User actor, Guid id, string format, string purpose = "download", string? parts = null, string? template = null,
        string? translation = null) =>
        (await CreateAsync(actor, id, format, purpose, ExportParts.Parse(parts), template, translation)).File;

    /// <summary>
    /// Emails the exported file as an attachment. The addresses are checked first; nothing is sent if one is not a valid
    /// address. The recipients, the file's fingerprint and the mail service's reference go into the audit trail.
    /// </summary>
    public async Task<string> EmailAsync(User actor, Guid id, string format, string recipients, string? note, CancellationToken ct = default,
        string? parts = null, string? template = null, string? translation = null)
    {
        if (email is null) throw new DomainException("Email is not available.");
        var to = ParseRecipients(recipients);
        var chosen = ExportParts.Parse(parts);
        if (note is { Length: > 2000 }) throw new DomainException("The message is too long. Keep it under 2,000 characters.");
        var (file, record, item) = await CreateAsync(actor, id, format, "email", chosen, template, translation);
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
    public async Task<ExportFile> DraftAsync(User actor, Guid id, string format, string? parts = null, string? template = null, string? translation = null)
    {
        var (file, record, item) = await CreateAsync(actor, id, format, "email-draft", ExportParts.Parse(parts), template, translation);
        var text = $"Please find attached {What(item)} \"{item.Name}\" ({record.Marker.ToLowerInvariant()}).\r\n"
            + "\r\nTo confirm the file has not been changed, it can be checked with \"Check an exported file\" in VerbaFlow.\r\n";
        var eml = EmlWriter.Write(new EmailMessage([], Subject(item, record), text, [new EmailAttachment(file.FileName, file.ContentType, file.Content)]), draft: true);
        return new ExportFile(eml, "message/rfc822", Path.ChangeExtension(file.FileName, ".eml"));
    }

    /// <summary>Whether this person can have exports put in their own Outlook drafts.</summary>
    // Made through VerbaFlow's link with the first organisation's Microsoft directory, so only for its people for now.
    public bool CanDraftInOutlook(User actor, string? userToken) => actor.OrganisationId is null && drafts?.CanUse(actor, userToken) == true;

    /// <summary>
    /// Puts a new email with the exported file attached in the person's own Outlook Drafts folder (owner chose "Outlook
    /// drafts", 2026-11-06). They add who it goes to and send it from Outlook, so VerbaFlow does not know the recipients;
    /// the file's fingerprint is recorded, so a recipient's copy can be checked.
    /// </summary>
    /// <param name="userToken">The person's Microsoft sign-in token, which the mail service exchanges to act as them.</param>
    public async Task<MailDraft> OutlookDraftAsync(User actor, string? userToken, Guid id, string format, string? parts = null, string? template = null,
        string? translation = null, CancellationToken ct = default)
    {
        if (!CanDraftInOutlook(actor, userToken)) throw new DomainException("Putting emails in your Outlook drafts needs a Microsoft sign-in. Use Open a new email instead.");
        return await MailDraftAsync(actor, id, format, parts, template, translation, "outlook-draft", "item.outlook_draft", "outlook_draft.failed", drafts.Name,
            "Your Microsoft account has no Outlook mailbox VerbaFlow can reach, so the draft could not be made. Use Open a new email instead.",
            m => drafts.CreateAsync(actor, userToken, m, ct));
    }

    /// <summary>Whether this person can have drafts made with their own browser's mail permission: Outlook.com (a personal Microsoft account) and Gmail.</summary>
    public (bool Outlook, bool Gmail) OwnDrafts(User actor) =>
        ownDrafts is null ? (false, false) : (actor.Individual && actor.SignInId?.StartsWith("microsoft:", StringComparison.Ordinal) == true, ownDrafts.Gmail);

    /// <summary>
    /// Puts the export in the person's own Outlook.com or Gmail drafts, with the mail permission their browser got for it
    /// (owner asked for other providers, 2026-11-06). The permission is used once and never kept.
    /// </summary>
    public async Task<MailDraft> OwnDraftAsync(User actor, string service, string? mailToken, Guid id, string format, string? parts = null, string? template = null,
        string? translation = null, CancellationToken ct = default)
    {
        var (outlook, gmail) = OwnDrafts(actor);
        if (service == MailServices.Gmail ? !gmail : service != MailServices.Outlook || !outlook)
            throw new DomainException("Putting emails in your drafts is not available here. Use Open a new email instead.");
        if (string.IsNullOrWhiteSpace(mailToken) || mailToken.Length > 8000) throw new DomainException("Your email account did not give permission. Try again.");
        var gm = service == MailServices.Gmail;
        return await MailDraftAsync(actor, id, format, parts, template, translation, gm ? "gmail-draft" : "outlook-draft",
            gm ? "item.gmail_draft" : "item.outlook_draft", gm ? "gmail_draft.failed" : "outlook_draft.failed", gm ? "gmail" : "microsoft-graph (own sign-in)",
            gm ? "Your Google account has no Gmail mailbox, so the draft could not be made. Use Open a new email instead."
               : "Your Microsoft account has no Outlook mailbox VerbaFlow can reach, so the draft could not be made. Use Open a new email instead.",
            m => ownDrafts!.CreateAsync(service, mailToken, m, ct));
    }

    private async Task<MailDraft> MailDraftAsync(User actor, Guid id, string format, string? parts, string? template, string? translation, string purpose,
        string auditType, string failedType, string serviceName, string noMailbox, Func<EmailMessage, Task<MailDraft>> make)
    {
        var (file, record, item) = await CreateAsync(actor, id, format, purpose, ExportParts.Parse(parts), template, translation);
        var text = $"Please find attached {What(item)} \"{item.Name}\" ({record.Marker.ToLowerInvariant()}).\r\n"
            + "\r\nTo confirm the file has not been changed, it can be checked with \"Check an exported file\" in VerbaFlow.\r\n";
        MailDraft draft;
        try
        {
            draft = await make(new EmailMessage([], Subject(item, record), text, [new EmailAttachment(file.FileName, file.ContentType, file.Content)]));
        }
        catch (Exception ex) when (ex is not OperationCanceledException && reporter is not null)
        {
            var failure = await reporter.ReportAsync(purpose, id, ex);
            await stores.Audit.AppendAsync(actor.Id, Capacity(actor, item), "speak", id, failedType,
                AuditChain.Details(("kind", ex is NoMailboxException ? "no mailbox" : failure.Kind.ToString()), ("reference", failure.Reference)));
            throw new DomainException(ex is NoMailboxException ? $"{noMailbox} (Reference {failure.Reference})" : failure.UserMessage);
        }
        await stores.Audit.AppendAsync(actor.Id, Capacity(actor, item), "speak", id, auditType,
            AuditChain.Details(("format", record.Format), ("marker", record.Marker), ("sha256", record.Sha256), ("mailService", serviceName),
                ("mailReference", draft.Reference.Length > 40 ? draft.Reference[..40] + "…" : draft.Reference)));
        return draft;
    }

    /// <summary>The heading over a dictation's text: what the person said it was for.</summary>
    /// <summary>A dictation's paragraphs, with "•" or "1." in front of list points.</summary>
    /// <param name="textOf">The text to print for a passage (a translation); its own text when left out.</param>
    public static List<MinutesPassage> DictationText(Transcript transcript, Func<Segment, string>? textOf = null)
    {
        textOf ??= s => s.Text;
        var pieces = DictationCommands.Paragraphs(transcript.Render(), r => r.Segment)
            // A line break typed while editing is a paragraph break too; only the first piece is the list point.
            .SelectMany(p => string.Join(" ", p.Select(r => textOf(r.Segment)).Where(x => x.Length > 0)).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select((text, k) => (text, list: k == 0 ? p[0].Segment.List : null)))
            .ToList();
        var markers = DictationCommands.ListMarkers(pieces.Select(x => x.list));
        return pieces.Select((x, i) => new MinutesPassage("", "", x.text, markers[i])).ToList();
    }

    public static string DocumentHeading(string? type) => ExportWords.English.DocumentHeading(type);

    private static string What(Item item) => item.IsDictation ? "the dictation" : "the minutes of";

    private static string Subject(Item item, ExportRecord record) =>
        $"{(item.IsDictation ? "Dictation" : "Meeting minutes")}: {item.Name}" + (record.Marker == "Approved" ? "" : $" ({record.Marker.ToUpperInvariant()})");

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
        IReadOnlyList<string>? parts, string? templateId = null, string? translationLanguage = null)
    {
        format = format.ToLowerInvariant() switch { "docx" or "word" => ExportFormats.Word, "pdf" => ExportFormats.Pdf,
            _ => throw new DomainException("Choose Word or PDF.") };
        var item = await stores.RequireItemAsync(id);
        PermissionEvaluator.Require(actor, item, Capability.Export);
        // A dictation is just its text: there is no summary, minutes or tone, and no speakers or times to show.
        var dictation = item.IsDictation;
        // Meetings are laid out with a minutes template: the one chosen, else the default one, else VerbaFlow's standard.
        var templates = new MinutesTemplateService(stores, clock);
        // The organisation's templates and letterhead are not used for a recording in a personal account.
        var template = dictation ? null : item.Individual ? MinutesTemplate.Standard : await templates.PickAsync(item.Org, templateId switch
        {
            null or "" => null,
            "standard" => Guid.Empty,
            var x => Guid.TryParse(x, out var g) ? g : throw new DomainException("That minutes template no longer exists. Choose another one."),
        });
        if (dictation) parts = [ExportParts.Transcript];
        // Nothing chosen: the parts the template ticks.
        parts ??= ExportParts.All.Where(p => template!.Sections.Any(s => s.Part == p && s.On)).ToList();
        if (item.Status == ItemStatus.Purged) throw new NotFoundException("This recording has been deleted.");
        var transcript = await stores.Transcripts.GetAsync(id) ?? throw new DomainException("There is no transcript to export yet.");
        var outputs = (await stores.Outputs.GetAsync(id))?.Outputs;
        // A translation replaces the words, never the facts: details, marker and approval stay those of the recording.
        var translation = string.IsNullOrWhiteSpace(translationLanguage) ? null
            : await TranslationService.ForExportAsync(stores, item, transcript, dictation ? null : outputs, translationLanguage);
        var translated = translation?.Passages.ToDictionary(p => p.SegmentId, p => p.Text);
        string TextOf(Segment s) => translated is null ? s.Text : translated.GetValueOrDefault(s.Id, s.Text);
        if (translation is not null && outputs is not null)
            outputs = outputs with { Summary = translation.Summary ?? "", ActionPoints = translation.ActionPoints, Minutes = translation.Minutes ?? "",
                ToneOfMeeting = translation.Tone ?? "" };
        var all = await stores.Items.ListAsync();
        var users = (await stores.Users.ListAsync()).ToDictionary(u => u.Id, u => u.Name);
        var superseded = all.Any(i => i.SupersedesItemId == id);
        var now = clock.GetUtcNow();

        // Headings, labels and dates are printed in the language the summary is written in.
        var w = ExportWords.For(translation?.Language ?? item.OutputLanguage);
        string When(DateTimeOffset at) => WhenIn(at, w);
        var marker = superseded ? "Superseded" : item.Status == ItemStatus.Completed ? "Approved" : "Draft";
        var markerLine = marker switch
        {
            "Approved" => string.Format(w.ApprovedBy, Name(users, item.ApprovedBy), When(item.ApprovedAt!.Value)),
            "Superseded" => w.SupersededLine,
            _ => w.NotApprovedLine,
        };
        var ownerLabel = item.Source == SourceKind.Imported ? w.ImportedBy : w.RecordedBy;
        var details = new List<(string, string)>
        {
            (item.Source == SourceKind.Imported ? w.ImportedOn : w.RecordedOn, When(item.CreatedAt)),
            (w.Length, w.LengthOf(item.LengthMs)),
            (ownerLabel, Name(users, item.OwnerId)),
        };
        if (!string.IsNullOrWhiteSpace(item.ClientReference)) details.Add((w.ClientReference, item.ClientReference));
        if (!string.IsNullOrWhiteSpace(item.Description)) details.Add((w.Description, item.Description));
        details.Add((w.Languages, Languages(item.SpokenLanguages, w)));
        var attached = (await stores.Attachments.ListAsync()).Where(a => a.ChainId == item.ChainId).OrderBy(a => a.AddedAt).Select(a => a.FileName).ToList();
        if (attached.Count > 0) details.Add((w.AttachedFiles, string.Join(", ", attached) + " " + w.KeptInVerbaFlow));
        details.Add((w.Version, string.Format(w.VersionValue, item.VersionNo, transcript.Current.No)));
        details.Add((w.Exported, string.Format(w.ExportedValue, When(now), actor.Name)));
        if (translation is not null) details.Add((w.Translation, w.MachineTranslated));
        var everything = parts.Count == ExportParts.All.Count;
        if (!everything && !dictation) // in the template's order, under its headings
            details.Add((w.Contains, string.Join(w.Code is "zh" or "ja" ? "、" : ", ",
                template!.Sections.Where(s => parts.Contains(s.Part)).Select(s => MinutesTemplateService.HeadingIn(s, w)))));
        bool Has(string part) => parts.Contains(part);

        var shortRef = item.Id.ToString("N")[..8].ToUpperInvariant();
        // Approved PDFs carry a box saying who approved them and when, with the visible digital signature in it.
        var approval = marker == "Approved" && format == ExportFormats.Pdf ? new ApprovalStamp(Name(users, item.ApprovedBy), When(item.ApprovedAt!.Value), w) : null;
        var doc = new MinutesDocument(
            template is null ? item.Name : MinutesTemplateService.TitleFor(template, item.Name, item.CreatedAt, w), marker, markerLine, details,
            Has(ExportParts.Summary) ? outputs?.Summary : null,
            Has(ExportParts.Actions) ? outputs?.ActionPoints ?? [] : [],
            Has(ExportParts.Minutes) ? (outputs?.Minutes ?? "").Split('\n').Select(l => l.TrimEnd()).Where(l => l.Length > 0).ToList() : [],
            Has(ExportParts.Tone) ? outputs?.ToneOfMeeting : null,
            Has(ExportParts.Transcript)
                ? dictation
                    ? DictationText(transcript, TextOf)
                    : transcript.Render().Where(r => r.Segment.Text.Length > 0).Select(r => new MinutesPassage(Clock(r.Segment.StartMs), r.SpeakerName, TextOf(r.Segment))).ToList()
                : [],
            template is null ? $"VerbaFlow · {item.Name} · {w.Marker(marker)} · {w.Reference} {shortRef}" : MinutesTemplateService.Footer(template, item.Name, marker, shortRef, w),
            dictation ? w.DocumentHeading(item.DocumentType) : w.Transcript,
            Approval: approval, Words: w);

        byte[] bytes;
        // Recorded in History so a plain letter can be explained: which letterhead was used, or why none.
        string? letterheadUsed = dictation ? "none (not marked as a letter, Word document or email)" : null;
        string? signOffUsed = null;
        if (dictation && item.DocumentType is "letter" or "document" or "email")
        {
            // A dictated letter, Word document or email goes out on the organisation's letterhead when one is set.
            var kind = item.DocumentType;
            var letterDate = item.ApprovedAt ?? item.CreatedAt;
            var reference = string.IsNullOrWhiteSpace(item.ClientReference) ? null : item.ClientReference.Trim();
            var letter = new LetterDocument(item.Name,
                kind == "letter" ? w.Date(TimeZoneInfo.ConvertTime(letterDate, TimeZoneInfo.Local)) : null,
                reference,
                Joined(marker switch { "Approved" => null, "Superseded" => w.LetterSuperseded, _ => w.LetterDraft },
                    translation is null ? null : w.MachineTranslated),
                doc.Transcript.Select(t => t.Text).ToList(), doc.Transcript.Select(t => t.Marker).ToList(),
                Heading: kind switch { "document" => item.Name, "email" => w.Subject + " " + item.Name, _ => null },
                RefLabel: kind == "letter" ? w.OurRef : w.RefLabel,
                LargeHeading: kind == "document", Approval: approval, Words: w);
            if (kind is "letter" or "email")
            {
                // The author's sign-off goes under the letter or email; their signature picture only once it is approved.
                var signOffs = new SignOffService(stores, clock);
                var authorSignOff = await signOffs.ForAsync(item.OwnerId);
                var picture = authorSignOff is null ? null : await signOffs.PictureAsync(authorSignOff);
                SignOffLayout.Fitted fitted;
                if (translation is null) fitted = SignOffLayout.Fit(letter.Paragraphs, letter.Markers, authorSignOff, picture, marker == "Approved");
                else
                {
                    // The dictated closing is found in the original wording, then the translated paragraphs are cut at the same place.
                    var original = DictationText(transcript).Select(p => p.Text).ToList();
                    if (authorSignOff is not null && translation.ClosingFrom == authorSignOff.Closing && translation.ClosingTo is { } closing)
                        authorSignOff = authorSignOff with { Closing = closing };
                    var onOriginal = SignOffLayout.Fit(original, null, authorSignOff, picture, marker == "Approved");
                    var cut = onOriginal.Paragraphs.Count;
                    fitted = original.Count != letter.Paragraphs.Count
                        ? SignOffLayout.Fit(letter.Paragraphs, letter.Markers, authorSignOff, picture, marker == "Approved")
                        : new SignOffLayout.Fitted(letter.Paragraphs.Take(cut).ToList(), letter.Markers?.Take(cut).ToList(),
                            onOriginal.SignOff is { } so && cut < original.Count ? so with { Closing = letter.Paragraphs[cut].Trim() } : onOriginal.SignOff,
                            onOriginal.Note);
                }
                letter = letter with { Paragraphs = fitted.Paragraphs, Markers = fitted.Markers, SignOff = fitted.SignOff };
                signOffUsed = fitted.Note;
            }
            var letterheads = new Services.LetterheadService(stores, clock);
            var l = item.Individual ? Letterhead.Empty : await letterheads.GetAsync(item.Org);
            letterheadUsed = item.Individual ? "none (personal account)" : (format == ExportFormats.Word ? l.Word : l.Pdf) is null
                ? (format == ExportFormats.Word ? "none (no Word letterhead uploaded)" : "none (no PDF letterhead uploaded)")
                : (format == ExportFormats.Word ? "Word letterhead" : "PDF letterhead");
            bytes = format == ExportFormats.Word
                ? LetterWriter.Word(letter, item.Individual ? null : await letterheads.ReadAsync(Services.LetterheadService.Word, item.Org))
                : LetterWriter.Pdf(letter, item.Individual ? null : await letterheads.ReadAsync(Services.LetterheadService.Pdf, item.Org),
                    new LetterSpace(l.PdfTopFirstMm, l.PdfTopNextMm, l.PdfBottomMm, l.PdfSideMm, l.PdfRepeatFirstPage));
        }
        else if (template is not null) (bytes, letterheadUsed) = await templates.RenderAsync(doc, template, format, parts, item.Org);
        else bytes = format == ExportFormats.Word ? DocxMinutesWriter.Write(doc) : PdfMinutesWriter.Write(doc);
        // Recorded in History: who signed the PDF, or why it went out unsigned.
        string? signature = null, signatureNote = null;
        if (format == ExportFormats.Pdf && signer is not null)
        {
            try
            {
                bytes = await signer.SignAsync(bytes, $"Exported from VerbaFlow ({marker.ToLowerInvariant()})", w);
                signature = signer.Signature.Describe;
            }
            catch (Exception ex) when (reporter is not null)
            {
                // The file still goes out, unsigned; the fingerprint below still lets it be checked.
                var failure = await reporter.ReportAsync("signing", id, ex);
                signatureNote = $"not signed ({failure.Reference})";
            }
        }
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var record = new ExportRecord(Guid.NewGuid(), id, item.VersionNo, transcript.Current.No, format, marker, sha, bytes.LongLength, actor.Id, now,
            string.Join(",", parts), signature, template?.Name, translation?.Language);
        await stores.Exports.UpsertAsync(record.Id, record);
        (string, object?)[] audit = [("format", format), ("marker", marker), ("purpose", purpose), ("parts", string.Join(",", parts)),
            ("transcriptVersion", transcript.Current.No), ("sha256", sha), ("bytes", bytes.LongLength)];
        if (template is not null) audit = [.. audit, ("template", template.Name)];
        if (translation is not null) audit = [.. audit, ("translation", translation.Language), ("translationEngine", translation.Engine)];
        if (letterheadUsed is not null) audit = [.. audit, ("letterhead", letterheadUsed)];
        if (signOffUsed is not null) audit = [.. audit, ("signOff", signOffUsed)];
        if (signature is not null || signatureNote is not null) audit = [.. audit, ("signature", signature ?? signatureNote)];
        await stores.Audit.AppendAsync(actor.Id, Capacity(actor, item), "speak", id, "item.exported", AuditChain.Details(audit));

        var safeName = SafeFileName(item.Name);
        if (safeName.Length == 0) safeName = "meeting";
        if (safeName.Length > 80) safeName = safeName[..80];
        return (new ExportFile(bytes, ExportFormats.ContentType(format), $"{safeName} ({marker.ToLowerInvariant()}{(translation is null ? "" : ", " + translation.Language)}).{format}"), record, item);
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
            $"This file is unchanged. It is the {(match.Format == ExportFormats.Word ? "Word" : "PDF")} file exported by {by} on {When(match.ExportedAt)} ({match.Marker.ToLowerInvariant()}, version {version})."
                + (match.Signature is null ? "" : " It carries VerbaFlow's digital signature."),
            sha, match.ExportedAt, by, match.Marker, match.Format);
    }

    private static string? Joined(string? a, string? b) => a is null ? b : b is null ? a : a + " " + b;

    private static string Name(Dictionary<Guid, string> users, Guid? id) => id is { } x ? users.GetValueOrDefault(x, "someone") : "someone";

    private static string Capacity(User actor, Item item) => PermissionEvaluator.RoleOn(actor, item) switch
    {
        ItemRole.Owner => item.Source == SourceKind.Imported ? "importer" : "author",
        ItemRole.Assignee => "assignee",
        ItemRole.Attendee => "attendee",
        ItemRole.Colleague => "colleague",
        ItemRole.DepartmentAdmin => "department admin",
        _ => PermissionEvaluator.AdminOf(actor, item) ? "admin" : "user"
    };

    /// <summary>Dates in the computer's own time zone, with the offset so the time is never ambiguous.</summary>
    private static string When(DateTimeOffset t) => WhenIn(t, ExportWords.English);

    internal static string WhenIn(DateTimeOffset t, ExportWords w)
    {
        var local = TimeZoneInfo.ConvertTime(t, TimeZoneInfo.Local);
        var offset = local.Offset;
        var zone = offset == TimeSpan.Zero ? "UTC" : $"UTC{(offset < TimeSpan.Zero ? "-" : "+")}{offset:hh\\:mm}";
        return local.ToString(w.DateTimeFormat, w.Culture) + " " + zone;
    }

    private static string Clock(int ms) => TimeSpan.FromMilliseconds(ms) is var t && t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

    private static string Languages(string codes, ExportWords w) => codes == "auto" ? w.DetectedAutomatically
        : string.Join(", ", codes.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(c => SpokenLanguageCatalogue.NameOf(c.Trim())));

    /// <summary>
    /// A file name every computer accepts, whatever the server runs on: the characters Windows refuses (&lt; &gt; : " / \ | ? *
    /// and control characters) become _, and trailing dots and spaces go. A meeting called "&lt;Brad&gt;&lt;Rich&gt;" stays readable.
    /// </summary>
    public static string SafeFileName(string name)
    {
        var s = new string((name ?? "").Select(c => c < ' ' || "<>:\"/\\|?*".Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.', ' ');
        return s.Length == 0 ? "export" : s.Length > 120 ? s[..120].TrimEnd('.', ' ') : s;
    }
}
