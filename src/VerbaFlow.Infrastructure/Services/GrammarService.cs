using VerbaFlow.Core.Audit;
using VerbaFlow.Core.Domain;
using VerbaFlow.Core.Security;
using VerbaFlow.Core.Providers;
using VerbaFlow.Core.Transcripts;

namespace VerbaFlow.Infrastructure.Services;

/// <summary>A suggested correction of one passage: its text as checked, the suggestion, and why.</summary>
public sealed record GrammarSuggestion(Guid SegmentId, string Text, string Suggested, string Reason);

/// <param name="LeftOut">Suggestions not shown because they rewrote the passage instead of correcting it.</param>
public sealed record GrammarCheckView(string Engine, int PassagesChecked, IReadOnlyList<GrammarSuggestion> Suggestions, int LeftOut);

/// <summary>
/// Check grammar (owner chose "Suggest, I accept", 2026-10-13): suggestions for a dictation's or Speech item's text.
/// Nothing changes until the person accepts a change; an accepted change is an ordinary tracked edit.
/// </summary>
public sealed class GrammarService(Stores stores, IGrammarChecker checker, TimeProvider clock, FailureReporter reporter)
{
    private const string Product = "speak";
    /// <summary>A suggestion that keeps less of the wording than this was a rewrite, not a correction, and is left out.</summary>
    public const double MinWordingKept = 0.6;

    public string EngineName => checker.Name;

    public async Task<GrammarCheckView> CheckAsync(User actor, Guid id, CancellationToken ct = default)
    {
        var item = await stores.RequireItemAsync(id);
        PermissionEvaluator.Require(actor, item, Capability.Write);
        if (!item.IsDictation) throw new DomainException("Check grammar is for dictations and Speech items.");
        if (item.Processing == ProcessingState.Running) throw new DomainException("Wait for the text to be ready first.");
        var t = await stores.Transcripts.GetAsync(id) ?? throw new NotFoundException("The transcript is not ready yet.");
        // Saved text is the company's own wording, so a passage holding some is left alone, as are empty ones.
        var passages = t.Render().Select(r => r.Segment).Where(s => !string.IsNullOrWhiteSpace(s.Text) && s.Inserted is null).ToList();
        var byKey = passages.Select((s, i) => ($"p{i + 1}", s)).ToDictionary(x => x.Item1, x => x.s);
        IReadOnlyList<GrammarFix> fixes;
        try { fixes = passages.Count == 0 ? [] : await checker.CheckAsync(byKey.Select(kv => new GrammarPassage(kv.Key, kv.Value.Text)).ToList(), item.OutputLanguage, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var rep = await reporter.ReportAsync("grammar", id, ex);
            await Audit(actor, item, id, "grammar.failed", ("kind", rep.Kind.ToString()), ("reference", rep.Reference));
            throw new DomainException(rep.UserMessage);
        }
        var suggestions = new List<GrammarSuggestion>();
        var leftOut = 0;
        foreach (var f in fixes.GroupBy(f => f.Id).Select(g => g.First()))
        {
            if (!byKey.TryGetValue(f.Id, out var s)) continue;
            var suggested = f.Text.Trim();
            if (WordDiff.Words(suggested).SequenceEqual(WordDiff.Words(s.Text))) continue;
            if (WordDiff.WordingKept(s.Text, suggested) < MinWordingKept) { leftOut++; continue; }
            suggestions.Add(new GrammarSuggestion(s.Id, s.Text, suggested, string.IsNullOrWhiteSpace(f.Reason) ? "Suggested correction" : f.Reason));
        }
        await Audit(actor, item, id, "grammar.checked", ("engine", checker.Name), ("transcriptVersion", t.Current.No),
            ("passagesChecked", passages.Count), ("suggestions", suggestions.Count), ("leftOutAsRewrites", leftOut));
        return new GrammarCheckView(checker.Name, passages.Count, suggestions, leftOut);
    }

    /// <summary>
    /// Saves the person's accepted changes to one passage as a tracked edit. <paramref name="expected"/> is the passage's
    /// text as they last saw it, so a passage changed meanwhile is never overwritten. Returns the passage as now saved.
    /// </summary>
    public async Task<(Guid SegmentId, string Text)> ApplyAsync(User actor, Guid id, Guid segmentId, string expected, string text)
    {
        var item = await stores.RequireItemAsync(id);
        PermissionEvaluator.Require(actor, item, Capability.Write);
        var t = await stores.Transcripts.GetAsync(id) ?? throw new NotFoundException("The transcript is not ready yet.");
        var current = t.Render().Select(r => r.Segment).FirstOrDefault(s => s.Id == segmentId);
        if (current is null || current.Text != expected)
            throw new DomainException("This passage has changed since the check. Choose Check grammar again.");
        var index = t.Current.SegmentIds.IndexOf(segmentId);
        var v = t.EditSegment(segmentId, text, actor.Id, CapacityOf(actor, item), clock.GetUtcNow(), "Grammar suggestion accepted");
        await stores.Transcripts.UpsertAsync(id, t);
        var saved = t.Segments[v.SegmentIds[index]];
        await Audit(actor, item, id, "grammar.accepted", ("version", v.No), ("segmentId", segmentId));
        return (saved.Id, saved.Text);
    }

    private static string CapacityOf(User actor, Item item) => PermissionEvaluator.RoleOn(actor, item) switch
    {
        ItemRole.Owner => item.Source == SourceKind.Imported ? "importer" : "author",
        ItemRole.Assignee => "assignee",
        _ => actor.IsAdmin ? "admin" : "user"
    };

    private Task Audit(User actor, Item item, Guid id, string type, params (string, object?)[] details) =>
        stores.Audit.AppendAsync(actor.Id, CapacityOf(actor, item), Product, id, type, AuditChain.Details(details));
}
