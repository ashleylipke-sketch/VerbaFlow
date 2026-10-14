using System.Text.RegularExpressions;
using VerbaFlow.Core.Audit;
using VerbaFlow.Core.Domain;

namespace VerbaFlow.Infrastructure.Services;

public sealed record WidgetStart(Guid Id, Guid LiveId, string Ticket, string Engine);
public sealed record WidgetSessionRow(Guid Id, string? User, string Site, string Device, string Languages, DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt, string Outcome, int Listens, int? SpokenMs, int? Words);
public sealed record WidgetPolicyView(IReadOnlyList<string> BlockedSites, bool CanChange, string? ChangedBy, DateTimeOffset? ChangedAt);

/// <summary>
/// Widget: dictation into a box on any website, through the browser extension. The words are heard live exactly as in
/// Speech mode, but nothing is kept: no item, no text, no audio. The live session is thrown away when the person accepts
/// or cancels, and the history holds only facts (site, times, outcome, counts), checked so that no text can get in.
/// </summary>
public sealed partial class WidgetService(Stores stores, LiveSessions live, TimeProvider clock)
{
    private const string Product = "widget";
    public const int MaxBlockedSites = 500;

    /// <summary>The host of a web address, lower case and without "www.": what the Widget records and admins block.</summary>
    public static string SiteOf(string? address)
    {
        var a = (address ?? "").Trim().ToLowerInvariant();
        if (Uri.TryCreate(a.Contains("://") ? a : "https://" + a, UriKind.Absolute, out var u)) a = u.Host;
        a = a.TrimEnd('.');
        if (a.StartsWith("www.")) a = a[4..];
        return a.Length is > 0 and <= 253 && HostPattern().IsMatch(a) ? a : "";
    }

    public static bool IsBlocked(string site, IEnumerable<string> blocked) =>
        blocked.Any(b => site == b || site.EndsWith("." + b, StringComparison.Ordinal));

    /// <summary>A short browser and system label; anything else is dropped.</summary>
    public static string DeviceOf(string? device)
    {
        var d = DeviceChars().Replace((device ?? "").Trim(), "");
        return d.Length == 0 ? "unknown" : d.Length > 60 ? d[..60] : d;
    }

    public async Task<WidgetPolicyView> GetPolicyAsync(User actor)
    {
        var p = await stores.WidgetPolicy.GetAsync(WidgetPolicy.RowId) ?? new WidgetPolicy([]);
        var by = p.ChangedBy is { } id ? (await stores.Users.GetAsync(id))?.Name : null;
        return new WidgetPolicyView(p.BlockedSites, actor.IsAdmin, by, p.ChangedAt);
    }

    public async Task<WidgetPolicyView> SetBlockedSitesAsync(User actor, IEnumerable<string>? sites)
    {
        if (!actor.IsAdmin) throw new ForbiddenException("Only administrators can change where the Widget is switched off.");
        var clean = new List<string>();
        foreach (var raw in sites ?? [])
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var s = SiteOf(raw);
            if (s.Length == 0) throw new DomainException($"\"{raw.Trim()}\" is not a website address. Use a form like mail.example.com or example.com.");
            if (!clean.Contains(s)) clean.Add(s);
        }
        if (clean.Count > MaxBlockedSites) throw new DomainException($"At most {MaxBlockedSites} websites can be listed.");
        var before = (await stores.WidgetPolicy.GetAsync(WidgetPolicy.RowId))?.BlockedSites ?? [];
        clean.Sort(StringComparer.Ordinal);
        await stores.WidgetPolicy.UpsertAsync(WidgetPolicy.RowId, new WidgetPolicy(clean, actor.Id, clock.GetUtcNow()));
        await Audit(actor, "admin", "widget.policy_changed", ("blockedSites", clean.Count),
            ("added", string.Join(",", clean.Except(before))), ("removed", string.Join(",", before.Except(clean))));
        return await GetPolicyAsync(actor);
    }

    /// <summary>The person starts speaking into a box on <paramref name="site"/>. Refused where an administrator switched the Widget off.</summary>
    public async Task<WidgetStart> StartAsync(User actor, string? site, string? device, string? spokenLanguages)
    {
        var s = SiteOf(site);
        if (s.Length == 0) throw new DomainException("The Widget only works on websites.");
        var policy = await stores.WidgetPolicy.GetAsync(WidgetPolicy.RowId);
        if (policy is not null && IsBlocked(s, policy.BlockedSites))
            throw new DomainException("Your administrator has switched the Widget off on this website.");
        var (session, ticket) = await live.StartAsync(actor, spokenLanguages);
        var w = new WidgetSession(Guid.NewGuid(), actor.Id, s, DeviceOf(device), string.Join(",", session.Languages), clock.GetUtcNow(), session.Id);
        await stores.WidgetSessions.UpsertAsync(w.Id, w);
        await Audit(actor, "user", "widget.started", ("session", w.Id), ("site", s), ("device", w.Device), ("languages", w.Languages));
        return new WidgetStart(w.Id, session.Id, ticket, live.EngineName);
    }

    /// <summary>
    /// "Speak more" after stopping: a new live connection for the same box. The words heard before are already in the
    /// person's browser, so the earlier live session is thrown away now.
    /// </summary>
    public async Task<WidgetStart> ListenAgainAsync(User actor, Guid id)
    {
        var w = await RequireOpenAsync(actor, id);
        live.Discard(actor, w.LiveId);
        var (session, ticket) = await live.StartAsync(actor, w.Languages);
        await stores.WidgetSessions.UpsertAsync(id, w with { LiveId = session.Id, Listens = w.Listens + 1 });
        return new WidgetStart(id, session.Id, ticket, live.EngineName);
    }

    /// <summary>Accepted or cancelled: the live session and everything it heard is thrown away; only counts are kept.</summary>
    public async Task EndAsync(User actor, Guid id, string? outcome, int? spokenMs, int? words, int? reconnects)
    {
        var w = await RequireOpenAsync(actor, id);
        if (outcome is not ("accepted" or "cancelled")) throw new DomainException("Say whether the text was accepted or cancelled.");
        live.Discard(actor, w.LiveId);
        var ended = w with
        {
            EndedAt = clock.GetUtcNow(), Outcome = outcome, SpokenMs = Clamp(spokenMs, 24 * 3_600_000),
            Words = Clamp(words, 1_000_000), Reconnects = Clamp(reconnects, 10_000),
        };
        await stores.WidgetSessions.UpsertAsync(id, ended);
        await Audit(actor, "user", outcome == "accepted" ? "widget.accepted" : "widget.cancelled", ("session", id), ("site", w.Site),
            ("listens", w.Listens), ("spokenMs", ended.SpokenMs), ("words", ended.Words), ("reconnects", ended.Reconnects),
            ("secondsOpen", (int)(ended.EndedAt!.Value - w.StartedAt).TotalSeconds));
    }

    /// <summary>The person's own uses, newest first; administrators see everyone's.</summary>
    public async Task<IReadOnlyList<WidgetSessionRow>> ListAsync(User actor, int take = 100)
    {
        var users = (await stores.Users.ListAsync()).ToDictionary(u => u.Id, u => u.Name);
        var now = clock.GetUtcNow();
        return (await stores.WidgetSessions.ListAsync()).Where(w => actor.IsAdmin || w.UserId == actor.Id)
            .OrderByDescending(w => w.StartedAt).Take(take)
            .Select(w => new WidgetSessionRow(w.Id, actor.IsAdmin ? users.GetValueOrDefault(w.UserId) : null, w.Site, w.Device, w.Languages,
                w.StartedAt, w.EndedAt, w.Outcome ?? (now - w.StartedAt > TimeSpan.FromHours(6) ? "not finished" : "open"), w.Listens, w.SpokenMs, w.Words))
            .ToList();
    }

    private async Task<WidgetSession> RequireOpenAsync(User actor, Guid id)
    {
        var w = await stores.WidgetSessions.GetAsync(id) ?? throw new NotFoundException("That Widget session does not exist.");
        if (w.UserId != actor.Id) throw new ForbiddenException("This Widget session belongs to someone else.");
        if (w.Outcome is not null) throw new DomainException("This Widget session has already ended.");
        return w;
    }

    private static int? Clamp(int? v, int max) => v is null ? null : Math.Clamp(v.Value, 0, max);

    /// <summary>
    /// The content check (data model: "a schema check rejects any Widget event whose details contain text"): only these
    /// keys, only numbers, ids, site addresses, device labels and language codes.
    /// </summary>
    public static void RequireContentFree((string Key, object? Value)[] details)
    {
        foreach (var (k, v) in details)
        {
            var ok = k switch
            {
                "session" => v is Guid,
                "site" => v is string s && (s.Length == 0 || SiteOf(s) == s),
                "added" or "removed" => v is string s && s.Split(',', StringSplitOptions.RemoveEmptyEntries).All(x => SiteOf(x) == x),
                "device" => v is string s && DeviceOf(s) == s,
                "languages" => v is string s && LanguageList().IsMatch(s),
                "blockedSites" or "listens" or "spokenMs" or "words" or "reconnects" or "secondsOpen" => v is null or int,
                _ => false,
            };
            if (!ok) throw new InvalidOperationException($"Widget history may not hold \"{k}\" in that form: Widget keeps no text.");
        }
    }

    private Task Audit(User actor, string capacity, string type, params (string, object?)[] details)
    {
        RequireContentFree(details);
        return stores.Audit.AppendAsync(actor.Id, capacity, Product, null, type, AuditChain.Details(details));
    }

    [GeneratedRegex("^[a-z0-9]([a-z0-9-]*[a-z0-9])?(\\.[a-z0-9]([a-z0-9-]*[a-z0-9])?)*$")]
    private static partial Regex HostPattern();
    [GeneratedRegex("[^A-Za-z0-9 .()/-]")]
    private static partial Regex DeviceChars();
    [GeneratedRegex("^[A-Za-z]{2,3}(-[A-Za-z0-9]{2,4})*(,[A-Za-z]{2,3}(-[A-Za-z0-9]{2,4})*)*$")]
    private static partial Regex LanguageList();
}
