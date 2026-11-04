using System.Globalization;
using System.Text;
using VerbaFlow.Core.Domain;
using VerbaFlow.Core.Security;

namespace VerbaFlow.Infrastructure.Services;

/// <summary>One place a search matched: a transcript passage (with its time and speaker) or the summary, minutes, action points or details.</summary>
/// <param name="Marks">Where the search words are in <see cref="Text"/>: pairs of start and length.</param>
public sealed record SearchHit(string Where, string? Speaker, int? StartMs, Guid? SegmentId, string Text, IReadOnlyList<int[]> Marks);

public sealed record SearchResult(Guid Id, string Name, ItemMode Mode, string Status, string? Author, DateTimeOffset CreatedOn,
    string? ClientReference, IReadOnlyList<SearchHit> Hits, int MoreHits);

public sealed record SearchAnswer(IReadOnlyList<SearchResult> Results, bool Limited, string? Problem);

/// <summary>
/// Finds the recordings someone can open by what was said in them: the current transcript (with edits), the summary,
/// minutes and action points, and the name, client reference and description. Every word must appear somewhere in the
/// recording; "words in quotes" must appear together. Capitals and accents are ignored, and a word matches the start of
/// a longer word ("contract" finds "contracts"). Deleted recordings and older versions are left out.
/// Reads everything on each search, which is fine for one organisation's recordings; a search index (Azure AI Search)
/// would take over if that ever becomes slow.
/// </summary>
public sealed class SearchService(Stores stores)
{
    public const int MaxQuery = 200;
    public const int MaxTerms = 10;
    public const int MaxResults = 50;
    public const int HitsShown = 3;
    private const int Around = 70;

    public async Task<SearchAnswer> SearchAsync(User actor, string? query, ItemMode? mode = null)
    {
        var terms = Terms(query);
        if (terms.Count == 0) return new SearchAnswer([], false, (query ?? "").Trim().Length == 0 ? null : "Type at least two letters to search.");
        var items = await stores.Items.ListAsync();
        var superseded = items.Where(i => i.SupersedesItemId is not null).Select(i => i.SupersedesItemId!.Value).ToHashSet();
        var users = (await stores.Users.ListAsync()).ToDictionary(u => u.Id);
        var results = new List<SearchResult>();
        foreach (var item in items.OrderByDescending(i => i.CreatedAt))
        {
            if (item.Status is ItemStatus.Purged || superseded.Contains(item.Id) || !PermissionEvaluator.CanSee(actor, item)) continue;
            if (mode is { } m && item.Mode != m) continue;
            var hits = new List<(SearchHit Hit, int Terms)>();
            var found = new HashSet<int>();
            void Look(string where, string? speaker, int? start, Guid? seg, string? text)
            {
                if (string.IsNullOrWhiteSpace(text)) return;
                var marks = Find(text, terms, out var which);
                if (marks.Count == 0) return;
                found.UnionWith(which);
                var (snippet, shifted) = Snippet(text, marks);
                hits.Add((new SearchHit(where, speaker, start, seg, snippet, shifted), which.Count));
            }
            Look("Details", null, null, null, string.Join(" · ", new[] { item.Name, item.ClientReference, item.Description }.Where(x => !string.IsNullOrWhiteSpace(x))));
            if (await stores.Transcripts.GetAsync(item.Id) is { } t)
                foreach (var r in t.Render())
                    Look("Transcript", item.IsDictation ? null : r.SpeakerName, r.Segment.StartMs, r.Segment.Id, r.Segment.Text);
            if (!item.IsDictation && await stores.Outputs.GetAsync(item.Id) is { } o)
            {
                Look("Summary", null, null, null, o.Outputs.Summary);
                Look("Minutes", null, null, null, o.Outputs.Minutes);
                Look("Action points", null, null, null, string.Join("\n", o.Outputs.ActionPoints));
            }
            if (found.Count < terms.Count) continue; // every word must be somewhere in the recording
            // Passages with the most of the search words first, then in the order they come.
            var ordered = hits.Select((h, n) => (h.Hit, h.Terms, n)).OrderByDescending(h => h.Terms).ThenBy(h => h.Hit.Where == "Details").ThenBy(h => h.n).Select(h => h.Hit).ToList();
            results.Add(new SearchResult(item.Id, item.Name, item.Mode, StatusLabels.For(item.Mode, item.Status),
                users.GetValueOrDefault(item.OwnerId)?.Name, item.CreatedAt, item.ClientReference,
                ordered.Take(HitsShown).ToList(), Math.Max(0, ordered.Count - HitsShown)));
            if (results.Count == MaxResults) return new SearchAnswer(results, true, null);
        }
        return new SearchAnswer(results, false, null);
    }

    /// <summary>The words and "quoted phrases" of a search, folded (lower case, no accents). Single letters are ignored; single digits are kept ("clause 5").</summary>
    public static IReadOnlyList<string> Terms(string? query)
    {
        var q = (query ?? "").Trim();
        if (q.Length > MaxQuery) q = q[..MaxQuery];
        var terms = new List<string>();
        var parts = q.Split('"');
        for (var i = 0; i < parts.Length; i++)
        {
            var words = parts[i].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .Select(w => w.Trim('.', ',', ';', ':', '!', '?', '(', ')', '\'', '‘', '’', '“', '”')).Where(w => w.Length > 0);
            if (i % 2 == 1 && i < parts.Length - 1) { var phrase = string.Join(' ', words); if (phrase.Length > 0) terms.Add(phrase); }
            else terms.AddRange(words);
        }
        return terms.Select(Fold).Where(t => t.Length >= 2 || t.All(char.IsDigit)).Distinct().Take(MaxTerms).ToList();
    }

    /// <summary>Lower case without accents, one character for one character, so positions in the folded text are positions in the original.</summary>
    public static string Fold(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            var d = c.ToString().Normalize(NormalizationForm.FormD);
            var b = d.Length > 0 && CharUnicodeInfo.GetUnicodeCategory(d[0]) != UnicodeCategory.NonSpacingMark ? d[0] : c;
            sb.Append(char.ToLowerInvariant(b));
        }
        return sb.ToString();
    }

    /// <summary>Where the terms start a word in the text (spaces in a phrase match any run of spaces).</summary>
    public static List<int[]> Find(string text, IReadOnlyList<string> terms, out HashSet<int> which)
    {
        var folded = Fold(text);
        var marks = new List<int[]>();
        which = [];
        for (var n = 0; n < terms.Count; n++)
        {
            var pattern = System.Text.RegularExpressions.Regex.Escape(terms[n]).Replace("\\ ", "\\s+");
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(folded, $@"(?<![\p{{L}}\p{{N}}]){pattern}"))
            {
                var end = m.Index + m.Length; // mark the whole word: "contract" lights up "contracts"
                while (end < folded.Length && char.IsLetterOrDigit(folded[end])) end++;
                marks.Add([m.Index, end - m.Index]);
                which.Add(n);
            }
        }
        marks.Sort((a, b) => a[0].CompareTo(b[0]));
        return marks;
    }

    /// <summary>The text itself when short, else a stretch around the first match, cut at spaces, with the marks moved to fit.</summary>
    public static (string Text, List<int[]> Marks) Snippet(string text, List<int[]> marks)
    {
        var flat = text.Replace('\n', ' ').Replace('\r', ' ');
        if (flat.Length <= Around * 3) return (flat, marks);
        var from = Math.Max(0, marks[0][0] - Around);
        var to = Math.Min(flat.Length, marks[0][0] + marks[0][1] + Around * 2);
        if (from > 0) { var sp = flat.IndexOf(' ', from); if (sp >= 0 && sp < marks[0][0]) from = sp + 1; }
        if (to < flat.Length) { var sp = flat.LastIndexOf(' ', to); if (sp > marks[0][0] + marks[0][1]) to = sp; }
        var pre = from > 0 ? "…" : "";
        var shown = pre + flat[from..to] + (to < flat.Length ? "…" : "");
        var moved = marks.Where(m => m[0] >= from && m[0] + m[1] <= to).Select(m => new[] { m[0] - from + pre.Length, m[1] }).ToList();
        return (shown, moved);
    }
}
