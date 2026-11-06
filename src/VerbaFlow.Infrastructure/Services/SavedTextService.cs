using System.Security.Cryptography;
using System.Text;
using VerbaFlow.Core.Audit;
using VerbaFlow.Core.Domain;
using VerbaFlow.Core.Transcripts;

namespace VerbaFlow.Infrastructure.Services;

/// <summary>
/// Saved text: phrases, sentences or paragraphs a person drops into a dictation by saying "insert" and the code
/// ("insert legal 1.1"). Anyone signed in can read it; only administrators change it. Every change is audited with a
/// fingerprint of the text, so it can be shown later which wording was in force. Someone with a personal account has saved
/// text of their own instead (SavedText.OwnerId), which they change themselves.
/// </summary>
public sealed class SavedTextService(Stores stores, TimeProvider clock)
{
    public const int MaxEntries = 500;
    public const int MaxCodeLength = 60;
    public const int MaxCodeWords = 6;
    public const int MaxTextLength = 20_000;

    /// <summary>The saved text the person uses: the organisation's, or their own with a personal account (null = the organisation's).</summary>
    public async Task<IReadOnlyList<SavedText>> ListAsync(User? actor = null) =>
        (await InAsync(stores, actor?.Space)).OrderBy(t => SpokenCodes.Key(t.Code), StringComparer.Ordinal).ToList();

    /// <summary>The entries of one list of saved text: the organisation's (null) or a personal account's.</summary>
    public static async Task<List<SavedText>> InAsync(Stores stores, Guid? space) =>
        (await stores.SavedText.ListAsync()).Where(t => t.OwnerId == space).ToList();

    public async Task<SavedText> AddAsync(User actor, string? code, string? text)
    {
        RequireAdmin(actor);
        var (cleanCode, key) = CleanCode(code);
        var cleanText = CleanText(text);
        var existing = await InAsync(stores, actor.Space);
        if (existing.Count >= MaxEntries) throw new DomainException($"There is no room for more saved text ({MaxEntries} entries). Remove some first.");
        if (existing.FirstOrDefault(t => SpokenCodes.Key(t.Code) == key) is { } same)
            throw new DomainException($"\"{same.Code}\" already uses that code. Choose a different one.");
        var entry = new SavedText(Guid.NewGuid(), cleanCode, cleanText, actor.Id, clock.GetUtcNow(), OwnerId: actor.Space);
        await stores.SavedText.UpsertAsync(entry.Id, entry);
        await Audit(actor, "saved-text.added", ("code", cleanCode), ("sha256", Fingerprint(cleanText)), ("characters", cleanText.Length));
        return entry;
    }

    public async Task<SavedText> ChangeAsync(User actor, Guid id, string? code, string? text)
    {
        RequireAdmin(actor);
        var entry = await RequireAsync(actor, id);
        var (cleanCode, key) = CleanCode(code);
        var cleanText = CleanText(text);
        if ((await InAsync(stores, actor.Space)).FirstOrDefault(t => t.Id != id && SpokenCodes.Key(t.Code) == key) is { } same)
            throw new DomainException($"\"{same.Code}\" already uses that code. Choose a different one.");
        var changed = entry with { Code = cleanCode, Text = cleanText, ChangedBy = actor.Id, ChangedAt = clock.GetUtcNow() };
        await stores.SavedText.UpsertAsync(id, changed);
        await Audit(actor, "saved-text.changed", ("code", cleanCode), ("previousCode", entry.Code == cleanCode ? null : entry.Code),
            ("sha256", Fingerprint(cleanText)), ("previousSha256", Fingerprint(entry.Text)), ("characters", cleanText.Length));
        return changed;
    }

    public async Task RemoveAsync(User actor, Guid id)
    {
        RequireAdmin(actor);
        var entry = await RequireAsync(actor, id);
        await stores.SavedText.DeleteAsync(id);
        await Audit(actor, "saved-text.removed", ("code", entry.Code), ("sha256", Fingerprint(entry.Text)));
    }

    /// <summary>Short fingerprint of the wording, for the audit trail and the processing history.</summary>
    public static string Fingerprint(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private async Task<SavedText> RequireAsync(User actor, Guid id) =>
        await stores.SavedText.GetAsync(id) is { } e && e.OwnerId == actor.Space ? e : throw new NotFoundException("That saved text does not exist.");

    private static void RequireAdmin(User actor)
    {
        // Someone with a personal account changes their own saved text; the organisation's is changed by its administrators.
        if (!actor.Individual && !actor.IsAdmin) throw new ForbiddenException("Only administrators can change saved text.");
    }

    /// <summary>The code as typed (spaces tidied) and its comparable form. It must start with a word, so "insert 1.1" is never a code on its own.</summary>
    public static (string Code, string Key) CleanCode(string? code)
    {
        var clean = string.Join(' ', (code ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (clean.Length == 0) throw new DomainException("Enter the code you will say after \"insert\", for example legal 1.1.");
        if (clean.Length > MaxCodeLength) throw new DomainException($"Codes can be at most {MaxCodeLength} characters.");
        if (clean.Any(char.IsControl)) throw new DomainException("Codes cannot contain control characters.");
        var key = SpokenCodes.Key(clean);
        if (key.Length == 0 || !char.IsLetter(key[0])) throw new DomainException("A code must start with a word, for example legal 1.1 or disclaimer 2.0.");
        if (key.Split(' ').Length > MaxCodeWords) throw new DomainException($"Keep codes short: at most {MaxCodeWords} words and numbers.");
        if (key.Split(' ')[0] is "insert" or "invoeg") throw new DomainException("A code cannot start with the word \"insert\".");
        return (clean, key);
    }

    /// <summary>Keeps line breaks (they separate paragraphs); removes other control and private-use characters; at most one blank line in a row.</summary>
    public static string CleanText(string? text)
    {
        var lines = (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n')
            .Select(l => new string(l.Where(c => !char.IsControl(c) && char.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.PrivateUse
                && c is not ('\u2028' or '\u2029' or '\u0085')).ToArray()).Trim());
        var kept = new List<string>();
        foreach (var l in lines)
            if (l.Length > 0 || (kept.Count > 0 && kept[^1].Length > 0)) kept.Add(l);
        var clean = string.Join('\n', kept).Trim();
        if (clean.Length == 0) throw new DomainException("Enter the text to insert.");
        if (clean.Length > MaxTextLength) throw new DomainException($"Saved text can be at most {MaxTextLength:N0} characters.");
        return clean;
    }

    private Task Audit(User actor, string type, params (string, object?)[] details) =>
        stores.Audit.AppendAsync(actor.Id, actor.Individual ? "user" : "admin", "platform", null, type, AuditChain.Details(details));
}
