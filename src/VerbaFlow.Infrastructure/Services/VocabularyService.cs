using VerbaFlow.Core.Domain;
using VerbaFlow.Core.Audit;

namespace VerbaFlow.Infrastructure.Services;

public sealed record ImportResult(int Added, IReadOnlyList<string> Skipped);

/// <summary>
/// The company's custom vocabulary: names and terms the speech service should expect. Anyone signed in can read it;
/// only administrators change it. Every change is recorded in the audit trail. Someone with a personal account has a
/// vocabulary of their own instead (VocabularyTerm.OwnerId), which they change themselves.
/// </summary>
public sealed class VocabularyService(Stores stores, TimeProvider clock)
{
    /// <summary>Azure advises keeping a phrase list under 2,000 phrases; longer lists slow things down and lower quality.</summary>
    public const int MaxTerms = 2000;
    public const int MaxLength = 100;
    public const int MaxNoteLength = 200;

    /// <summary>The vocabulary the person uses: the organisation's, or their own with a personal account (null = the organisation's).</summary>
    public async Task<IReadOnlyList<VocabularyTerm>> ListAsync(User? actor = null) =>
        (await InAsync(stores, actor?.Space)).OrderBy(t => t.Text, StringComparer.CurrentCultureIgnoreCase).ToList();

    /// <summary>The terms of one vocabulary: the organisation's (null) or a personal account's.</summary>
    public static async Task<List<VocabularyTerm>> InAsync(Stores stores, Guid? space) =>
        (await stores.Vocabulary.ListAsync()).Where(t => t.OwnerId == space).ToList();

    /// <summary>The words sent to the speech service.</summary>
    public async Task<IReadOnlyList<string>> PhrasesAsync(User? actor = null) => (await ListAsync(actor)).Select(t => t.Text).ToList();

    public async Task<VocabularyTerm> AddAsync(User actor, string text, string? note)
    {
        RequireAdmin(actor);
        var clean = Normalise(text);
        var existing = await InAsync(stores, actor.Space);
        if (existing.Count >= MaxTerms) throw new DomainException($"The vocabulary is full ({MaxTerms} terms). Remove some before adding more.");
        if (existing.Any(t => Same(t.Text, clean))) throw new DomainException($"\"{clean}\" is already in the vocabulary.");
        var term = new VocabularyTerm(Guid.NewGuid(), clean, TrimNote(note), actor.Id, clock.GetUtcNow(), actor.Space);
        await stores.Vocabulary.UpsertAsync(term.Id, term);
        await Audit(actor, "vocabulary.added", ("term", clean));
        return term;
    }

    /// <summary>Adds many terms at once, one per line. Bad or duplicate lines are skipped and reported, not fatal.</summary>
    public async Task<ImportResult> ImportAsync(User actor, string lines)
    {
        RequireAdmin(actor);
        var existing = (await InAsync(stores, actor.Space)).Select(t => t.Text).ToList();
        var skipped = new List<string>();
        var added = new List<string>();
        foreach (var raw in (lines ?? "").Split('\n'))
        {
            var line = raw.Trim().TrimEnd('\r');
            if (line.Length == 0) continue;
            string clean;
            try { clean = Normalise(line); }
            catch (DomainException) { skipped.Add(Shorten(line) + " (not a valid term)"); continue; }
            if (existing.Any(e => Same(e, clean))) { skipped.Add(clean + " (already there)"); continue; }
            if (existing.Count >= MaxTerms) { skipped.Add(clean + " (vocabulary full)"); continue; }
            var term = new VocabularyTerm(Guid.NewGuid(), clean, null, actor.Id, clock.GetUtcNow(), actor.Space);
            await stores.Vocabulary.UpsertAsync(term.Id, term);
            existing.Add(clean); added.Add(clean);
        }
        if (added.Count > 0) await Audit(actor, "vocabulary.imported", ("added", added.Count), ("skipped", skipped.Count));
        return new ImportResult(added.Count, skipped);
    }

    public async Task RemoveAsync(User actor, Guid id)
    {
        RequireAdmin(actor);
        var term = await stores.Vocabulary.GetAsync(id) is { } found && found.OwnerId == actor.Space ? found : throw new NotFoundException("That term does not exist.");
        await stores.Vocabulary.DeleteAsync(id);
        await Audit(actor, "vocabulary.removed", ("term", term.Text));
    }

    private static void RequireAdmin(User actor)
    {
        // Someone with a personal account changes their own vocabulary; the organisation's is changed by its administrators.
        if (!actor.Individual && !actor.IsAdmin) throw new ForbiddenException("Only administrators can change the vocabulary.");
    }

    /// <summary>Trims and collapses spaces. Rejects empty, over-long or multi-line text and control characters.</summary>
    public static string Normalise(string? text)
    {
        var clean = string.Join(' ', (text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (clean.Length == 0) throw new DomainException("Enter a name or term.");
        if (clean.Length > MaxLength) throw new DomainException($"Terms can be at most {MaxLength} characters.");
        if (clean.Any(char.IsControl)) throw new DomainException("Terms cannot contain control characters.");
        return clean;
    }

    private static string? TrimNote(string? note)
    {
        var n = note?.Trim();
        if (string.IsNullOrEmpty(n)) return null;
        return n.Length > MaxNoteLength ? n[..MaxNoteLength] : n;
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.CurrentCultureIgnoreCase);
    private static string Shorten(string s) => s.Length > 40 ? s[..40] + "…" : s;

    private Task Audit(User actor, string type, params (string, object?)[] details) =>
        stores.Audit.AppendAsync(actor.Id, actor.Individual ? "user" : "admin", "platform", null, type, AuditChain.Details(details));
}
