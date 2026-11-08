using System.Collections.Concurrent;
using System.Security.Cryptography;
using VerbaFlow.Core.Audit;
using VerbaFlow.Core.Domain;
using VerbaFlow.Infrastructure.Services;

namespace VerbaFlow.Api;

/// <summary>
/// Links a Widget (browser extension, Windows, Android, iPhone) to a personal account (Google, or any account that signs in
/// to the website but not in the Widget itself). The Widget asks for a short code and opens the VerbaFlow website; the
/// person, signed in there, agrees; the Widget, which has been waiting with its own secret, then gets a VerbaFlow sign-in.
/// Codes last 10 minutes and are kept only in memory.
/// </summary>
public sealed class WidgetLinks(TimeProvider clock)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    private const string Letters = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // no 0/O or 1/I
    private readonly ConcurrentDictionary<string, Link> links = new();

    public sealed class Link(string code, string secretHash, string device, DateTimeOffset made)
    {
        public string Code { get; } = code;
        public string SecretHash { get; } = secretHash;
        public string Device { get; } = device;
        public DateTimeOffset Made { get; } = made;
        public Guid? UserId { get; set; }
        public bool Collected { get; set; }
    }

    public (string Code, string Secret) Start(string? device)
    {
        Sweep();
        if (links.Count > 2000) throw new DomainException("Too many Widgets are being linked at the moment. Try again in a few minutes.");
        var d = string.IsNullOrWhiteSpace(device) ? "Widget" : device.Trim();
        if (d.Length > 60) d = d[..60];
        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        while (true)
        {
            var code = new string(RandomNumberGenerator.GetItems<char>(Letters, 8));
            if (links.TryAdd(code, new Link(code, Hash(secret), d, clock.GetUtcNow()))) return (code[..4] + "-" + code[4..], secret);
        }
    }

    /// <summary>The waiting link for a code the person typed or followed, or null.</summary>
    public Link? Find(string? code)
    {
        var c = Clean(code);
        return c is not null && links.TryGetValue(c, out var l) && Fresh(l) && l.UserId is null ? l : null;
    }

    public void Approve(Link link, Guid userId) => link.UserId = userId;

    /// <summary>The person the Widget was linked to, once (later asks get nothing), or null while it is still waiting.</summary>
    public (bool Known, Guid? User) Collect(string? secret)
    {
        if (string.IsNullOrWhiteSpace(secret) || secret.Length > 200) return (false, null);
        var h = Hash(secret);
        var link = links.Values.FirstOrDefault(l => CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(l.SecretHash), System.Text.Encoding.ASCII.GetBytes(h)));
        if (link is null || !Fresh(link) || link.Collected) return (false, null);
        if (link.UserId is null) return (true, null);
        link.Collected = true;
        links.TryRemove(link.Code, out _);
        return (true, link.UserId);
    }

    private bool Fresh(Link l) => clock.GetUtcNow() - l.Made < Lifetime;
    private void Sweep() { foreach (var l in links.Values.Where(l => !Fresh(l))) links.TryRemove(l.Code, out _); }
    private static string Hash(string s) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(s)));
    private static string? Clean(string? code)
    {
        var c = new string((code ?? "").ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());
        return c.Length == 8 ? c : null;
    }
}

public sealed record WidgetLinkStart(string? Device);
public sealed record WidgetLinkPoll(string? Secret);

public static class WidgetLinkEndpoints
{
    public static void MapWidgetLinks(this WebApplication app, RouteGroupBuilder api)
    {
        var limit = new SignInProblemLimit();
        // Public: the Widget is not signed in yet.
        app.MapPost("/api/widget-link/start", (WidgetLinks links, WidgetLinkStart b) =>
        {
            if (!limit.Allow()) return Results.StatusCode(429);
            var (code, secret) = links.Start(b.Device);
            return Results.Ok(new { code, secret, expiresInSeconds = (int)WidgetLinks.Lifetime.TotalSeconds });
        });
        app.MapPost("/api/widget-link/poll", (WidgetLinks links, SessionTokens tokens, Stores stores, WidgetLinkPoll b) =>
        {
            var (known, user) = links.Collect(b.Secret);
            if (!known) return Results.Json(new { error = "The link code has run out. Start again in the Widget." }, statusCode: 410);
            if (user is null) return Results.Json(new { waiting = true }, statusCode: 202);
            return Results.Ok(new { token = tokens.Issue(user.Value) });
        });
        // Signed in on the website: what is waiting, and agreeing to it.
        api.MapGet("/widget-link/{code}", (HttpContext h, WidgetLinks links, string code) =>
            links.Find(code) is { } l ? Results.Ok(new { device = l.Device, code = l.Code[..4] + "-" + l.Code[4..] })
                : Results.Json(new { error = "That code is not known or has run out. Start again in the Widget to get a new one." }, statusCode: 404));
        api.MapPost("/widget-link/{code}/approve", async (HttpContext h, WidgetLinks links, Stores stores, string code) =>
        {
            var me = h.Current();
            if (!me.Individual)
                throw new DomainException("Your work account signs in to the Widget directly: in the Widget's settings, choose Sign in with Microsoft.");
            var l = links.Find(code) ?? throw new NotFoundException("That code is not known or has run out. Start again in the Widget to get a new one.");
            links.Approve(l, me.Id);
            await stores.Audit.AppendAsync(me.Id, "user", "widget", null, "widget.linked", AuditChain.Details(("device", l.Device)));
            return Results.Ok(new { device = l.Device });
        });
    }
}
