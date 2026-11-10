using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using VerbaFlow.Core.Audit;
using VerbaFlow.Core.Domain;
using VerbaFlow.Infrastructure.Export;

namespace VerbaFlow.Infrastructure.Services;

/// <summary>What an admin types into a minutes template (everything except the logo, which is uploaded on its own).</summary>
public sealed record MinutesTemplateInput(string? Name, string? Title, string? Font, string? Colour, string? LogoPlace, bool OnLetterhead,
    IReadOnlyList<TemplateSection>? Sections, bool TranscriptTimes, string? FooterText, bool IsDefault);

/// <summary>
/// Minutes templates: how exported meeting minutes look. Everyone can see the list (to pick one when exporting); only
/// admins add, change or remove them. A logo is kept under its SHA-256 like signature pictures. History records each
/// change by the template's name, never anything from a meeting.
/// </summary>
public sealed partial class MinutesTemplateService(Stores stores, TimeProvider clock)
{
    public const int MaxTemplates = 20, MaxName = 60, MaxTitle = 120, MaxHeading = 60, MaxFooter = 80;
    public const long MaxLogoBytes = 1024 * 1024;

    /// <summary>An organisation's templates (<see cref="User.Org"/>; null = the first organisation).</summary>
    public async Task<List<MinutesTemplate>> ListAsync(Guid? organisation = null) =>
        (await OfAsync(organisation)).OrderByDescending(t => t.IsDefault).ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>The template to export with: the one asked for, else the default one, else VerbaFlow's standard layout.</summary>
    public async Task<MinutesTemplate> PickAsync(Guid? organisation, Guid? id)
    {
        if (id is { } x && x != Guid.Empty)
            return await stores.MinutesTemplates.GetAsync(x) is { } found && found.OrganisationId == Organisations.Stored(organisation) ? found : throw new DomainException("That minutes template no longer exists. Choose another one.");
        if (id == Guid.Empty) return MinutesTemplate.Standard;
        return (await OfAsync(organisation)).FirstOrDefault(t => t.IsDefault) ?? MinutesTemplate.Standard;
    }

    public async Task<MinutesTemplate> CreateAsync(User actor, MinutesTemplateInput input)
    {
        RequireAdmin(actor);
        var all = await OfAsync(actor.Org);
        if (all.Count >= MaxTemplates) throw new DomainException($"There is no room for more templates ({MaxTemplates}). Remove one first.");
        var t = Check(input, all, null) with { Id = Guid.NewGuid(), OrganisationId = Organisations.Stored(actor.Org), CreatedBy = actor.Id, CreatedAt = clock.GetUtcNow() };
        // The first template made becomes the default, so making one is enough to change how minutes look.
        if (all.Count == 0) t = t with { IsDefault = true };
        await SaveAsync(t, all);
        await Audit(actor, "minutes-template.created", ("template", t.Name), ("templateId", t.Id), ("default", t.IsDefault));
        return t;
    }

    public async Task<MinutesTemplate> ChangeAsync(User actor, Guid id, MinutesTemplateInput input)
    {
        RequireAdmin(actor);
        var all = await OfAsync(actor.Org);
        var current = all.FirstOrDefault(t => t.Id == id) ?? throw new NotFoundException("That minutes template no longer exists.");
        var t = Check(input, all, id) with
        {
            Id = id, OrganisationId = current.OrganisationId, CreatedBy = current.CreatedBy, CreatedAt = current.CreatedAt, LogoSha256 = current.LogoSha256, LogoType = current.LogoType,
            ChangedBy = actor.Id, ChangedAt = clock.GetUtcNow(),
        };
        await SaveAsync(t, all);
        await Audit(actor, "minutes-template.changed", ("template", t.Name), ("templateId", t.Id), ("default", t.IsDefault),
            ("renamedFrom", current.Name == t.Name ? null : current.Name));
        return t;
    }

    public async Task RemoveAsync(User actor, Guid id)
    {
        RequireAdmin(actor);
        var t = await RequireAsync(actor, id);
        await stores.MinutesTemplates.DeleteAsync(id);
        await Audit(actor, "minutes-template.removed", ("template", t.Name), ("templateId", t.Id), ("wasDefault", t.IsDefault));
    }

    public async Task<MinutesTemplate> SetLogoAsync(User actor, Guid id, Stream content)
    {
        RequireAdmin(actor);
        var t = await RequireAsync(actor, id);
        using var ms = new MemoryStream();
        var buffer = new byte[81920];
        int n;
        while ((n = await content.ReadAsync(buffer)) > 0)
        {
            if (ms.Length + n > MaxLogoBytes) throw new DomainException("The logo is too big. Use a picture of at most 1 MB.");
            ms.Write(buffer, 0, n);
        }
        var bytes = ms.ToArray();
        var type = SignOffService.PictureType(bytes) ?? throw new DomainException("Use a PNG or JPEG picture for the logo.");
        try { using var check = PdfSharp.Drawing.XImage.FromStream(new MemoryStream(bytes)); if (check.PixelWidth < 1) throw new FormatException(); }
        catch { throw new DomainException("This picture cannot be read. Save it again as PNG or JPEG and try again."); }
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        Directory.CreateDirectory(stores.TemplateLogoRoot);
        var path = PathFor(sha, type);
        if (!File.Exists(path)) await File.WriteAllBytesAsync(path, bytes);
        var next = t with { LogoSha256 = sha, LogoType = type, ChangedBy = actor.Id, ChangedAt = clock.GetUtcNow() };
        await stores.MinutesTemplates.UpsertAsync(id, next);
        await Audit(actor, "minutes-template.logo_added", ("template", t.Name), ("templateId", t.Id), ("sha256", sha), ("bytes", bytes.LongLength));
        return next;
    }

    public async Task<MinutesTemplate> RemoveLogoAsync(User actor, Guid id)
    {
        RequireAdmin(actor);
        var t = await RequireAsync(actor, id);
        if (t.LogoSha256 is null) return t;
        var next = t with { LogoSha256 = null, LogoType = null, ChangedBy = actor.Id, ChangedAt = clock.GetUtcNow() };
        await stores.MinutesTemplates.UpsertAsync(id, next);
        await Audit(actor, "minutes-template.logo_removed", ("template", t.Name), ("templateId", t.Id), ("sha256", t.LogoSha256));
        return next;
    }

    /// <summary>The template's logo, or null.</summary>
    public async Task<byte[]?> LogoAsync(MinutesTemplate t)
    {
        if (t.LogoSha256 is null || t.LogoType is null) return null;
        var path = PathFor(t.LogoSha256, t.LogoType);
        return File.Exists(path) ? await File.ReadAllBytesAsync(path) : null;
    }

    /// <summary>
    /// A made-up meeting laid out with the template, so an admin can see how minutes will look before anyone exports.
    /// Not recorded as an export and not signed: it is not a real meeting.
    /// </summary>
    public async Task<ExportFile> SampleAsync(User actor, Guid id, string format)
    {
        var t = await PickAsync(actor.Org, id);
        format = format == ExportFormats.Word ? ExportFormats.Word : ExportFormats.Pdf;
        var date = TimeZoneInfo.ConvertTime(clock.GetUtcNow(), TimeZoneInfo.Local);
        var name = "Monthly team meeting";
        var doc = new MinutesDocument(
            TitleFor(t, name, date), "Draft", "Not approved yet. The text may still change. (This is a sample with made-up text.)",
            [("Recorded on", date.ToString("d MMMM yyyy, HH:mm", CultureInfo.InvariantCulture)), ("Length", "42 min 10 s"),
                ("Recorded by", actor.Name), ("Client reference", "ABC-123"), ("Languages", "English (United Kingdom)")],
            "The team reviewed last month's figures, agreed the plan for the new office and set dates for the next steps.",
            ["Sam to send the updated budget by Friday.", "Ann to book the movers for the 14th."],
            ["Figures:", "- Sales were 4% above plan.", "- Costs were in line with the budget.", "New office:", "- The move is planned for the 14th."],
            "Positive and focused; everyone took part.",
            [new("0:00", "Ann", "Good morning, everyone. Let's start with last month's figures."),
                new("0:12", "Sam", "Sales came in four percent above plan, and costs were on budget."),
                new("0:25", "Ann", "Good. Next, the new office. Can we confirm the 14th for the move?")],
            Footer(t, name, "Draft", "SAMPLE"), Style: null);
        var (bytes, _) = await RenderAsync(doc, t, format, t.Sections.Where(s => s.On).Select(s => s.Part).ToList(), actor.Org);
        return new ExportFile(bytes, ExportFormats.ContentType(format), $"Sample minutes ({ExportService.SafeFileName(t.Name)}).{format}");
    }

    /// <summary>
    /// Lays out minutes with a template: its order, headings, font, colour and logo, on the letterhead when it asks for one.
    /// Returns the file and, for History, which letterhead was used (or why none); null when the template does not use one.
    /// </summary>
    /// <param name="parts">The parts the person chose to include.</param>
    public async Task<(byte[] Bytes, string? Letterhead)> RenderAsync(MinutesDocument doc, MinutesTemplate t, string format, IReadOnlyList<string> parts, Guid? organisation = null)
    {
        var transcript = t.Sections.FirstOrDefault(s => s.Part == "transcript");
        var style = new MinutesStyle(t.Font, t.Colour, await LogoAsync(t), t.LogoPlace,
            t.Sections.Where(s => parts.Contains(s.Part)).Select(s => (s.Part, HeadingIn(s, doc.W))).ToList());
        doc = doc with
        {
            Style = style,
            // A dictation keeps its own heading (Letter, Document...); a meeting's transcript takes the template's.
            TranscriptHeading = transcript is null || doc.TranscriptHeading != doc.W.Transcript ? doc.TranscriptHeading : HeadingIn(transcript, doc.W),
            Transcript = t.TranscriptTimes ? doc.Transcript : doc.Transcript.Select(p => p with { Time = "" }).ToList(),
        };
        if (!t.OnLetterhead)
            return (format == ExportFormats.Word ? DocxMinutesWriter.Write(doc) : PdfMinutesWriter.Write(doc), null);
        var letterheads = new LetterheadService(stores, clock);
        var l = await letterheads.GetAsync(organisation);
        if (format == ExportFormats.Word)
        {
            var word = await letterheads.ReadAsync(LetterheadService.Word, organisation);
            return (DocxMinutesWriter.Write(doc, word), word is null ? "none (no Word letterhead uploaded)" : "Word letterhead");
        }
        var pdf = await letterheads.ReadAsync(LetterheadService.Pdf, organisation);
        return (PdfMinutesWriter.Write(doc, pdf, new LetterSpace(l.PdfTopFirstMm, l.PdfTopNextMm, l.PdfBottomMm, l.PdfSideMm, l.PdfRepeatFirstPage)),
            pdf is null ? "none (no PDF letterhead uploaded)" : "PDF letterhead");
    }

    /// <summary>The title line: {name} becomes the meeting's name and {date} the day it was recorded.</summary>
    public static string TitleFor(MinutesTemplate t, string name, DateTimeOffset recorded, ExportWords? words = null)
    {
        var day = (words ?? ExportWords.English).Date(TimeZoneInfo.ConvertTime(recorded, TimeZoneInfo.Local));
        var title = Placeholder().Replace(t.Title, m => m.Value.ToLowerInvariant() switch { "{name}" => name, "{date}" => day, _ => m.Value }).Trim();
        return title.Length == 0 ? name : title;
    }

    /// <summary>The footer: the template's text, then the meeting's name, marker and reference (always kept, so a printout can be traced).</summary>
    public static string Footer(MinutesTemplate t, string name, string marker, string reference, ExportWords? words = null)
    {
        var w = words ?? ExportWords.English;
        return string.Join(" · ", new[] { t.FooterText, name, w.Marker(marker), w.Reference + " " + reference }.Where(x => !string.IsNullOrWhiteSpace(x)));
    }

    /// <summary>A part's heading in the export's language: a heading left as VerbaFlow's English default is translated,
    /// one the admin typed is printed exactly as typed.</summary>
    public static string HeadingIn(TemplateSection s, ExportWords w) =>
        MinutesTemplate.StandardSections.Any(d => d.Part == s.Part && d.Heading == s.Heading) ? w.Heading(s.Part) : s.Heading;

    [GeneratedRegex(@"\{(name|date)\}", RegexOptions.IgnoreCase)]
    private static partial Regex Placeholder();

    [GeneratedRegex("^#[0-9a-fA-F]{6}$")]
    private static partial Regex HexColour();

    private static MinutesTemplate Check(MinutesTemplateInput i, List<MinutesTemplate> all, Guid? id)
    {
        var name = Tidy(i.Name);
        if (name.Length == 0) throw new DomainException("Give the template a name.");
        if (name.Length > MaxName) throw new DomainException($"Keep the name under {MaxName} characters.");
        if (name.Equals(MinutesTemplate.Standard.Name, StringComparison.OrdinalIgnoreCase) ||
            all.Any(t => t.Id != id && t.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            throw new DomainException($"There is already a template called {name}.");
        var title = Tidy(i.Title);
        if (title.Length == 0) title = "{name}";
        if (title.Length > MaxTitle) throw new DomainException($"Keep the title under {MaxTitle} characters.");
        var font = MinutesTemplate.Fonts.FirstOrDefault(f => f.Equals(Tidy(i.Font), StringComparison.OrdinalIgnoreCase))
            ?? throw new DomainException("Choose one of the fonts in the list.");
        var colour = Tidy(i.Colour);
        if (!HexColour().IsMatch(colour)) throw new DomainException("Choose a colour for the headings.");
        var place = Tidy(i.LogoPlace).ToLowerInvariant() is "right" ? "right" : "left";
        var footer = Tidy(i.FooterText);
        if (footer.Length > MaxFooter) throw new DomainException($"Keep the footer text under {MaxFooter} characters.");

        var sections = i.Sections ?? MinutesTemplate.StandardSections;
        var parts = MinutesTemplate.StandardSections.Select(s => s.Part).ToList();
        if (sections.Count != parts.Count || parts.Any(p => sections.Count(s => s.Part == p) != 1))
            throw new DomainException("The template must list Summary, Action points, Minutes, Tone and Transcript once each.");
        var cleaned = sections.Select(s =>
        {
            var heading = Tidy(s.Heading);
            if (heading.Length == 0) heading = MinutesTemplate.StandardSections.First(x => x.Part == s.Part).Heading;
            if (heading.Length > MaxHeading) throw new DomainException($"Keep each heading under {MaxHeading} characters.");
            return s with { Heading = heading };
        }).ToList();
        if (!cleaned.Any(s => s.On)) throw new DomainException("Tick at least one part to include.");
        return new MinutesTemplate(Guid.Empty, name, i.IsDefault, title, font, colour.ToUpperInvariant(), null, null, place, i.OnLetterhead,
            cleaned, i.TranscriptTimes, footer, Guid.Empty, default);
    }

    /// <summary>Saves a template; when it is the default, every other template stops being the default.</summary>
    private async Task SaveAsync(MinutesTemplate t, List<MinutesTemplate> all)
    {
        if (t.IsDefault)
            foreach (var other in all.Where(o => o.Id != t.Id && o.IsDefault))
                await stores.MinutesTemplates.UpsertAsync(other.Id, other with { IsDefault = false });
        await stores.MinutesTemplates.UpsertAsync(t.Id, t);
    }

    private async Task<MinutesTemplate> RequireAsync(User actor, Guid id) =>
        await stores.MinutesTemplates.GetAsync(id) is { } t && t.OrganisationId == Organisations.Stored(actor.Org) ? t
            : throw new NotFoundException("That minutes template no longer exists.");

    private async Task<List<MinutesTemplate>> OfAsync(Guid? organisation) =>
        (await stores.MinutesTemplates.ListAsync()).Where(t => t.OrganisationId == Organisations.Stored(organisation)).ToList();

    private string PathFor(string sha, string type) => Path.Combine(stores.TemplateLogoRoot, sha + (type == "png" ? ".png" : ".jpg"));

    private static string Tidy(string? text) => string.Join(' ', (text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static void RequireAdmin(User actor)
    {
        if (!actor.IsAdmin) throw new ForbiddenException("Only administrators can change minutes templates.");
    }

    private Task Audit(User actor, string action, params (string, object?)[] details) =>
        stores.Audit.AppendAsync(actor.Id, "admin", "platform", null, action, AuditChain.Details(Organisations.With(details, actor.Org)));
}
