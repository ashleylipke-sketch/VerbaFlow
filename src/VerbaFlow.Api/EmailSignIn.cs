using System.Collections.Concurrent;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using VerbaFlow.Core.Providers;
using VerbaFlow.Infrastructure.Services;

namespace VerbaFlow.Api;

/// <summary>
/// Sign-in with just an email address, for people without a Google or Microsoft account: VerbaFlow emails a 6-digit code,
/// the person types it in, and gets VerbaFlow's own sign-in (<see cref="SessionTokens"/>) for a personal account. No
/// passwords are kept. Codes last <see cref="Lifetime"/>, allow <see cref="MaxTries"/> wrong guesses, and live only in memory
/// (a restart of VerbaFlow forgets them); only a SHA-256 of each code is held.
/// </summary>
public sealed class EmailCodes(TimeProvider clock)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan Between = TimeSpan.FromSeconds(60);
    public const int MaxTries = 5, MaxPerHour = 5;
    private readonly ConcurrentDictionary<string, Pending> pending = new();
    private readonly ConcurrentDictionary<string, List<DateTimeOffset>> sent = new();

    private sealed class Pending(string hash, DateTimeOffset made)
    {
        public string Hash { get; } = hash;
        public DateTimeOffset Made { get; } = made;
        public int Wrong { get; set; }
    }

    /// <summary>The address in one form (trimmed, small letters), or null when it is not an email address.</summary>
    public static string? Clean(string? email)
    {
        var e = (email ?? "").Trim().ToLowerInvariant();
        if (e.Length is < 3 or > 254 || e.Any(char.IsWhiteSpace) || e.Count(c => c == '@') != 1) return null;
        try
        {
            var a = new MailAddress(e);
            return a.Address == e && a.Host.Contains('.') && !a.Host.StartsWith('.') && !a.Host.EndsWith('.') ? e : null;
        }
        catch (FormatException) { return null; }
    }

    /// <summary>A new code for this address, or null when one was sent too recently or too often.</summary>
    public string? Make(string email)
    {
        var now = clock.GetUtcNow();
        var times = sent.GetOrAdd(email, _ => []);
        lock (times)
        {
            times.RemoveAll(t => now - t > TimeSpan.FromHours(1));
            if (times.Count >= MaxPerHour || times.Count > 0 && now - times[^1] < Between) return null;
            times.Add(now);
        }
        if (sent.Count > 5000) foreach (var k in sent.Where(kv => { lock (kv.Value) return kv.Value.All(t => now - t > TimeSpan.FromHours(1)); }).Select(kv => kv.Key).ToList()) sent.TryRemove(k, out _);
        foreach (var k in pending.Where(kv => now - kv.Value.Made >= Lifetime).Select(kv => kv.Key).ToList()) pending.TryRemove(k, out _);
        var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
        pending[email] = new Pending(Hash(email, code), now);
        return code;
    }

    /// <summary>Whether the code is the one last sent to this address. A right code can be used once.</summary>
    public bool Check(string email, string? code)
    {
        var c = new string((code ?? "").Where(char.IsDigit).ToArray());
        if (!pending.TryGetValue(email, out var p)) return false;
        if (clock.GetUtcNow() - p.Made >= Lifetime) { pending.TryRemove(email, out _); return false; }
        lock (p)
        {
            if (p.Wrong >= MaxTries) return false;
            var ok = c.Length == 6 && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(p.Hash), Encoding.ASCII.GetBytes(Hash(email, c)));
            if (!ok) { if (++p.Wrong >= MaxTries) pending.TryRemove(email, out _); return false; }
        }
        pending.TryRemove(email, out _);
        return true;
    }

    private static string Hash(string email, string code) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(email + "\n" + code)));
}

public sealed record EmailCodeStart(string? Email);
public sealed record EmailCodeFinish(string? Email, string? Code, string? Name);

public static class EmailSignInEndpoints
{
    public static void MapEmailSignIn(this WebApplication app)
    {
        var limit = new SignInProblemLimit();
        app.MapPost("/api/sign-in/email/start", async (SignInOptions options, EmailCodes codes, IEmailSender mail, FailureReporter reporter, EntraUsers users,
            EmailCodeStart b, CancellationToken ct) =>
        {
            if (!options.EmailCodes) return Results.NotFound();
            if (!limit.Allow()) return Results.Json(new { error = "Too many people are signing in at the moment. Try again in a minute." }, statusCode: 429);
            var email = EmailCodes.Clean(b.Email);
            if (email is null) return Results.Json(new { error = "That does not look like an email address. Check it and try again." }, statusCode: 400);
            var code = codes.Make(email);
            if (code is null) return Results.Json(new { error = "A code was sent to that address a moment ago. Check your email (and the junk folder), or try again in a minute." }, statusCode: 429);
            // An address that signs in another way gets an email saying so instead of a code (one address, one way to sign in).
            // The page answers the same either way, so nobody learns from it whether an address has an account.
            var refused = await users.RefusalForAsync("email:" + email, email);
            try
            {
                await mail.SendAsync(refused is not null
                    ? new EmailMessage([email], "Signing in to VerbaFlow",
                        $"You asked for a code to sign in to VerbaFlow. {refused}\n\n" +
                        "If you did not ask to sign in to VerbaFlow, you can ignore this email.\n", [])
                    : new EmailMessage([email], $"Your VerbaFlow sign-in code: {code}",
                    $"Your code to sign in to VerbaFlow is:\n\n    {code}\n\nType it on the VerbaFlow page within 10 minutes.\n\n" +
                    "If you did not ask to sign in to VerbaFlow, you can ignore this email: nobody can sign in without the code.\n", []), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var rep = await reporter.ReportAsync("email-sign-in", null, ex);
                return Results.Json(new { error = $"The code could not be emailed. Try again later. (Reference {rep.Reference})" }, statusCode: 502);
            }
            return Results.Ok(new { sent = true, minutes = (int)EmailCodes.Lifetime.TotalMinutes });
        });
        app.MapPost("/api/sign-in/email/finish", async (SignInOptions options, EmailCodes codes, EntraUsers users, SessionTokens tokens, EmailCodeFinish b) =>
        {
            if (!options.EmailCodes) return Results.NotFound();
            if (!limit.Allow()) return Results.Json(new { error = "Too many people are signing in at the moment. Try again in a minute." }, statusCode: 429);
            var email = EmailCodes.Clean(b.Email);
            if (email is null || !codes.Check(email, b.Code))
                return Results.Json(new { error = "That code is not right, or it has run out. Check the latest email, or ask for a new code." }, statusCode: 401);
            var name = string.IsNullOrWhiteSpace(b.Name) ? null : b.Name.Trim();
            var user = await users.ResolvePersonalAsync(new PersonalIdentity("email:" + email, name, email, "email"));
            return Results.Ok(new { token = tokens.Issue(user.Id), name = user.Name });
        });
    }
}
