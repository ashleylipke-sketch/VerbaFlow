using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VerbaFlow.Core.Audit;
using VerbaFlow.Core.Domain;
using VerbaFlow.Core.Providers;
using VerbaFlow.Core.Security;
using VerbaFlow.Core.Transcripts;
using VerbaFlow.Infrastructure.Export;

namespace VerbaFlow.Infrastructure.Services;

public sealed record TranslationLanguage(string Code, string Name);

/// <param name="UpToDate">False when the text or summary changed after it was translated.</param>
public sealed record TranslationRow(string Language, string Name, string MadeBy, DateTimeOffset MadeAt, bool UpToDate, int Untranslated);

public sealed record TranslationsView(bool CanTranslate, IReadOnlyList<TranslationLanguage> Languages, IReadOnlyList<TranslationRow> Translations,
    string? WhyNot);

/// <summary>An original passage (or dictation paragraph) beside its translation.</summary>
public sealed record TranslatedLine(string? Time, string? Speaker, string Original, string Translated, string? Marker = null);

public sealed record TranslationPage(string ItemName, bool Dictation, TranslationRow Row, bool CanTranslate, string Engine,
    string? Summary, string? SummaryTranslated, IReadOnlyList<string> Actions, IReadOnlyList<string> ActionsTranslated,
    string? Minutes, string? MinutesTranslated, string? Tone, string? ToneTranslated, IReadOnlyList<TranslatedLine> Lines);

/// <summary>
/// Machine translation of a recording (owner chose "Translate", 2026-11-06): its text and, for a meeting, its summary,
/// action points, minutes and tone, into one of the languages exports have their own words for. The recording never
/// changes. A translation is made from one transcript version, so an edit afterwards makes it out of date; it can then be
/// made again. Making one needs the Export permission (it is a copy to send out, and each one is a small AI charge);
/// anyone who can open the recording can read it.
/// </summary>
public sealed class TranslationService(Stores stores, ITranslator translator, TimeProvider clock, FailureReporter reporter)
{
    private const string Product = "speak";

    /// <summary>The languages a recording can be translated into: those exports have their own words for, and English.</summary>
    public static readonly IReadOnlyList<TranslationLanguage> Languages =
    [
        new("en", "English"), new("af", "Afrikaans"), new("ar", "Arabic"), new("zh", "Chinese (Simplified)"), new("nl", "Dutch"),
        new("fr", "French"), new("de", "German"), new("it", "Italian"), new("ja", "Japanese"), new("pl", "Polish"),
        new("ru", "Russian"), new("es", "Spanish"), new("uk", "Ukrainian"),
    ];

    public static string NameOf(string code) => Languages.FirstOrDefault(l => l.Code == code)?.Name ?? code;

    private static string Base(string? code) => (code ?? "en").Split('-')[0].Trim().ToLowerInvariant();

    public async Task<TranslationsView> ListAsync(User actor, Guid id)
    {
        var item = await stores.RequireItemAsync(id);
        PermissionEvaluator.Require(actor, item, Capability.Read);
        var why = await WhyNotAsync(actor, item);
        var own = Base(item.OutputLanguage);
        var rows = await RowsAsync(item);
        return new TranslationsView(why is null, why is null ? Languages.Where(l => l.Code != own).ToList() : [], rows, why);
    }

    /// <summary>Why this person cannot translate this recording now, in a plain sentence; null when they can.</summary>
    private async Task<string?> WhyNotAsync(User actor, Item item)
    {
        if (!PermissionEvaluator.Evaluate(actor, item, Capability.Export).Allowed) return "You do not have permission to translate this recording.";
        if (item.Status == ItemStatus.Purged) return "This recording has been deleted.";
        if (item.Status == ItemStatus.Draft) return "Finish the dictation first.";
        if (item.Processing == ProcessingState.Running) return "Wait for the text to be ready first.";
        if (await stores.Transcripts.GetAsync(item.Id) is null) return "There is no text to translate yet.";
        return null;
    }

    private async Task<List<TranslationRow>> RowsAsync(Item item)
    {
        var list = await ForItemAsync(item.Id);
        if (list.Count == 0) return [];
        var users = (await stores.Users.ListAsync()).ToDictionary(u => u.Id, u => u.Name);
        var t = await stores.Transcripts.GetAsync(item.Id);
        var fp = Fingerprint((await stores.Outputs.GetAsync(item.Id))?.Outputs, item.IsDictation);
        return list.OrderBy(x => NameOf(x.Language)).Select(x => Row(x, users, t, fp)).ToList();
    }

    private static TranslationRow Row(Translation x, Dictionary<Guid, string> users, Transcript? t, string? fp) =>
        new(x.Language, NameOf(x.Language), users.GetValueOrDefault(x.MadeBy, "someone"), x.MadeAt,
            t is not null && t.Current.No == x.TranscriptVersionNo && x.OutputsFingerprint == fp, x.Untranslated);

    private async Task<List<Translation>> ForItemAsync(Guid id) => (await stores.Translations.ListAsync()).Where(x => x.ItemId == id).ToList();

    /// <summary>Makes (or makes again) the translation into <paramref name="language"/>.</summary>
    public async Task<TranslationRow> TranslateAsync(User actor, Guid id, string? language, CancellationToken ct = default)
    {
        var item = await stores.RequireItemAsync(id);
        PermissionEvaluator.Require(actor, item, Capability.Read);
        if (await WhyNotAsync(actor, item) is { } why) throw new DomainException(why);
        var code = Base(language);
        if (Languages.All(l => l.Code != code)) throw new DomainException("Choose a language to translate into.");
        if (code == Base(item.OutputLanguage)) throw new DomainException($"This recording is already in {NameOf(code)}.");
        var existing = await ForItemAsync(id);
        var old = existing.FirstOrDefault(x => x.Language == code);
        if (old is null && existing.Count >= Translation.MaxPerItem)
            throw new DomainException($"A recording can have at most {Translation.MaxPerItem} translations. Remove one first.");

        var t = (await stores.Transcripts.GetAsync(id))!;
        var outputs = item.IsDictation ? null : (await stores.Outputs.GetAsync(id))?.Outputs;
        var passages = t.Render().Select(r => r.Segment).Where(s => !string.IsNullOrWhiteSpace(s.Text)).ToList();
        var pieces = passages.Select((s, i) => new TextPiece($"p{i + 1}", s.Text)).ToList();
        if (outputs is not null)
        {
            if (!string.IsNullOrWhiteSpace(outputs.Summary)) pieces.Add(new("summary", outputs.Summary));
            pieces.AddRange(outputs.ActionPoints.Select((a, i) => new TextPiece($"a{i + 1}", a)).Where(p => p.Text.Length > 0));
            if (!string.IsNullOrWhiteSpace(outputs.Minutes)) pieces.Add(new("minutes", outputs.Minutes));
            if (!string.IsNullOrWhiteSpace(outputs.ToneOfMeeting)) pieces.Add(new("tone", outputs.ToneOfMeeting));
        }
        // A letter's or email's closing ("Yours sincerely,") is translated too, so the sign-off matches the letter.
        var signOff = item.IsDictation && item.DocumentType is "letter" or "email" ? await new SignOffService(stores, clock).ForAsync(item.OwnerId) : null;
        if (signOff is { Closing.Length: > 0 }) pieces.Add(new("closing", signOff.Closing));
        if (pieces.Count == 0) throw new DomainException("There is no text to translate yet.");

        IReadOnlyList<TextPiece> done;
        try { done = await translator.TranslateAsync(pieces, code, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var rep = await reporter.ReportAsync("translation", id, ex);
            await Audit(actor, item, "translation.failed", ("language", code), ("kind", rep.Kind.ToString()), ("reference", rep.Reference));
            throw new DomainException(rep.UserMessage);
        }
        var got = done.Where(p => !string.IsNullOrWhiteSpace(p.Text)).GroupBy(p => p.Id).ToDictionary(g => g.Key, g => g.First().Text.Trim());
        var untranslated = pieces.Count(p => !got.ContainsKey(p.Id));
        string Of(string key, string original) => got.GetValueOrDefault(key, original);

        var translation = new Translation(old?.Id ?? Guid.NewGuid(), id, code, t.Current.No, Fingerprint(outputs, item.IsDictation), translator.Name,
            actor.Id, clock.GetUtcNow(),
            passages.Select((s, i) => new TranslatedPassage(s.Id, Of($"p{i + 1}", s.Text))).ToList(),
            outputs is null || string.IsNullOrWhiteSpace(outputs.Summary) ? null : Of("summary", outputs.Summary),
            outputs?.ActionPoints.Select((a, i) => Of($"a{i + 1}", a)).ToList() ?? [],
            outputs is null || string.IsNullOrWhiteSpace(outputs.Minutes) ? null : Of("minutes", outputs.Minutes),
            outputs is null || string.IsNullOrWhiteSpace(outputs.ToneOfMeeting) ? null : Of("tone", outputs.ToneOfMeeting),
            untranslated)
        {
            ClosingFrom = signOff is { Closing.Length: > 0 } ? signOff.Closing : null,
            ClosingTo = signOff is { Closing.Length: > 0 } ? Of("closing", signOff.Closing) : null,
        };
        await stores.Translations.UpsertAsync(translation.Id, translation);
        await Audit(actor, item, "translation.made", ("language", code), ("engine", translator.Name), ("transcriptVersion", t.Current.No),
            ("pieces", pieces.Count), ("characters", pieces.Sum(p => p.Text.Length)), ("untranslated", untranslated == 0 ? null : untranslated),
            ("madeAgain", old is not null ? true : null));
        var users = (await stores.Users.ListAsync()).ToDictionary(u => u.Id, u => u.Name);
        return Row(translation, users, t, translation.OutputsFingerprint);
    }

    /// <summary>The translation beside the original, to read on its own page.</summary>
    public async Task<TranslationPage> GetAsync(User actor, Guid id, string language)
    {
        var item = await stores.RequireItemAsync(id);
        PermissionEvaluator.Require(actor, item, Capability.Read);
        var x = (await ForItemAsync(id)).FirstOrDefault(x => x.Language == Base(language))
            ?? throw new NotFoundException($"This recording has no {NameOf(Base(language))} translation.");
        var t = await stores.Transcripts.GetAsync(id) ?? throw new NotFoundException("The transcript is not ready yet.");
        var outputs = item.IsDictation ? null : (await stores.Outputs.GetAsync(id))?.Outputs;
        var users = (await stores.Users.ListAsync()).ToDictionary(u => u.Id, u => u.Name);
        var row = Row(x, users, t, Fingerprint(outputs, item.IsDictation));
        var text = x.Passages.ToDictionary(p => p.SegmentId, p => p.Text);
        // Passages edited since then have new ids: they show the current wording with nothing beside it.
        string TextOf(Segment s) => text.GetValueOrDefault(s.Id, "");
        List<TranslatedLine> lines;
        if (item.IsDictation)
        {
            var paragraphs = DictationCommands.Paragraphs(t.Render().Where(r => r.Segment.Text.Length > 0), r => r.Segment).ToList();
            var markers = DictationCommands.ListMarkers(paragraphs.Select(p => p[0].Segment.List));
            lines = paragraphs.Select((p, i) => new TranslatedLine(null, null, string.Join(" ", p.Select(r => r.Segment.Text)),
                string.Join(" ", p.Select(r => TextOf(r.Segment)).Where(s => s.Length > 0)), markers[i])).ToList();
        }
        else
            lines = t.Render().Where(r => r.Segment.Text.Length > 0)
                .Select(r => new TranslatedLine(Clock(r.Segment.StartMs), r.SpeakerName, r.Segment.Text, TextOf(r.Segment))).ToList();
        return new TranslationPage(item.Name, item.IsDictation, row, await WhyNotAsync(actor, item) is null, x.Engine,
            outputs?.Summary, x.Summary, outputs?.ActionPoints ?? [], x.ActionPoints, outputs?.Minutes, x.Minutes,
            outputs?.ToneOfMeeting, x.Tone, lines);
    }

    public async Task RemoveAsync(User actor, Guid id, string language)
    {
        var item = await stores.RequireItemAsync(id);
        PermissionEvaluator.Require(actor, item, Capability.Export);
        var x = (await ForItemAsync(id)).FirstOrDefault(x => x.Language == Base(language))
            ?? throw new NotFoundException($"This recording has no {NameOf(Base(language))} translation.");
        await stores.Translations.DeleteAsync(x.Id);
        await Audit(actor, item, "translation.removed", ("language", x.Language));
    }

    /// <summary>
    /// The translation an export uses. Refused when there is none, or when the text changed since it was made, so an
    /// exported translation always matches the recording as it is.
    /// </summary>
    public static async Task<Translation> ForExportAsync(Stores stores, Item item, Transcript t, AiOutputs? outputs, string language)
    {
        var code = Base(language);
        var x = (await stores.Translations.ListAsync()).FirstOrDefault(x => x.ItemId == item.Id && x.Language == code)
            ?? throw new DomainException($"This recording has no {NameOf(code)} translation. Make one first.");
        if (x.TranscriptVersionNo != t.Current.No || x.OutputsFingerprint != Fingerprint(outputs, item.IsDictation))
            throw new DomainException($"The text has changed since it was translated into {NameOf(code)}. Translate it again first.");
        return x;
    }

    /// <summary>Deletes the translations of every version of a recording (when it is deleted); returns how many.</summary>
    public static async Task<int> DeleteForItemsAsync(Stores stores, IReadOnlyCollection<Guid> itemIds)
    {
        var list = (await stores.Translations.ListAsync()).Where(x => itemIds.Contains(x.ItemId)).ToList();
        foreach (var x in list) await stores.Translations.DeleteAsync(x.Id);
        return list.Count;
    }

    internal static string? Fingerprint(AiOutputs? o, bool dictation)
    {
        if (dictation || o is null) return null;
        var json = JsonSerializer.Serialize(new { o.Summary, o.ActionPoints, o.Minutes, o.ToneOfMeeting });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
    }

    private static string Clock(int ms) => TimeSpan.FromMilliseconds(ms) is var t && t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

    private static string CapacityOf(User actor, Item item) => PermissionEvaluator.RoleOn(actor, item) switch
    {
        ItemRole.Owner => item.Source == SourceKind.Imported ? "importer" : "author",
        ItemRole.Assignee => "assignee",
        ItemRole.Attendee => "attendee",
        ItemRole.Colleague => "colleague",
        ItemRole.DepartmentAdmin => "department admin",
        _ => actor.IsAdmin ? "admin" : "user"
    };

    private Task Audit(User actor, Item item, string type, params (string, object?)[] details) =>
        stores.Audit.AppendAsync(actor.Id, CapacityOf(actor, item), Product, item.Id, type, AuditChain.Details(details));
}
