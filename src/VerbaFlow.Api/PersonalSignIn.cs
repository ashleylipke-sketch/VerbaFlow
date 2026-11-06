using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using VerbaFlow.Infrastructure.Services;

namespace VerbaFlow.Api;

/// <summary>
/// Sign-in with a Google account, for people using VerbaFlow on their own (students, people without a work account).
/// The browser gets a Google ID token from Google's own sign-in button; it is checked here once (Google's signature,
/// issuer, and that it was made for VerbaFlow's Google client id) and swapped for a VerbaFlow sign-in (<see cref="SessionTokens"/>).
/// </summary>
public sealed class GoogleSignIn
{
    private readonly string _clientId;
    private readonly IConfigurationManager<OpenIdConnectConfiguration> _google;

    public GoogleSignIn(string clientId, IConfigurationManager<OpenIdConnectConfiguration>? google = null)
    {
        _clientId = clientId;
        _google = google ?? new ConfigurationManager<OpenIdConnectConfiguration>("https://accounts.google.com/.well-known/openid-configuration",
            new OpenIdConnectConfigurationRetriever(), new HttpDocumentRetriever { RequireHttps = true });
    }

    /// <summary>Who the Google ID token names, or null when it is not a valid Google sign-in made for VerbaFlow.</summary>
    public async Task<PersonalIdentity?> CheckAsync(string? credential, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(credential) || credential.Length > 8000) return null;
        var config = await _google.GetConfigurationAsync(ct);
        var result = await new JsonWebTokenHandler().ValidateTokenAsync(credential, new TokenValidationParameters
        {
            ValidIssuers = ["accounts.google.com", "https://accounts.google.com"],
            ValidAudience = _clientId,
            IssuerSigningKeys = config.SigningKeys,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(2),
        });
        if (!result.IsValid) return null;
        string? Get(string type) => result.ClaimsIdentity.FindFirst(type)?.Value;
        var sub = Get("sub");
        if (string.IsNullOrWhiteSpace(sub)) return null;
        return new PersonalIdentity("google:" + sub, Get("name"), Get("email"), "Google");
    }
}

/// <summary>
/// VerbaFlow's own sign-in for people who signed in with Google: a value only this server can make or read (ASP.NET Data
/// Protection, keys kept in the data folder), naming the person and when it was made. It lasts <see cref="Lifetime"/> and the
/// web app swaps it for a fresh one each time it starts, so someone who uses VerbaFlow now and then stays signed in.
/// </summary>
public sealed class SessionTokens(IDataProtectionProvider protection, TimeProvider clock)
{
    public const string Prefix = "vf1.";
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(30);
    // The end time is inside the protected value and checked against VerbaFlow's own clock.
    private readonly IDataProtector _protector = protection.CreateProtector("VerbaFlow.SignIn.v1");

    public string Issue(Guid userId) =>
        Prefix + _protector.Protect(JsonSerializer.Serialize(new Payload(userId, clock.GetUtcNow(), clock.GetUtcNow() + Lifetime)));

    /// <summary>The person a token names, or null when it is not one of VerbaFlow's, has run out or was changed.</summary>
    public Guid? Read(string token)
    {
        if (!token.StartsWith(Prefix, StringComparison.Ordinal)) return null;
        try
        {
            var p = JsonSerializer.Deserialize<Payload>(_protector.Unprotect(token[Prefix.Length..]));
            return p is null || p.Ends < clock.GetUtcNow() ? null : p.User;
        }
        catch (Exception e) when (e is System.Security.Cryptography.CryptographicException or JsonException or FormatException) { return null; }
    }

    private sealed record Payload(Guid User, DateTimeOffset Made, DateTimeOffset Ends);
}

public sealed record GoogleSignInBody(string? Credential);

public static class PersonalSignInEndpoints
{
    /// <summary>The public Google sign-in exchange (outside the signed-in group) and the renewal of a VerbaFlow sign-in (inside it).</summary>
    public static void MapPersonalSignIn(this WebApplication app, RouteGroupBuilder api)
    {
        var limit = new SignInProblemLimit();
        app.MapPost("/api/sign-in/google", async (HttpContext h, SignInOptions options, [Microsoft.AspNetCore.Mvc.FromServices] GoogleSignIn? google, EntraUsers users, SessionTokens tokens,
            GoogleSignInBody b, CancellationToken ct) =>
        {
            if (google is null || !options.Google) return Results.NotFound();
            if (!limit.Allow()) return Results.StatusCode(429);
            var who = await google.CheckAsync(b.Credential, ct);
            if (who is null) return Results.Json(new { error = "The Google sign-in did not work. Try again." }, statusCode: 401);
            var user = await users.ResolvePersonalAsync(who);
            return Results.Ok(new { token = tokens.Issue(user.Id), name = user.Name });
        });
        // A fresh VerbaFlow sign-in for someone already signed in with one (Google accounts); Microsoft sign-ins renew with Microsoft.
        api.MapPost("/sign-in/renew", (HttpContext h, SessionTokens tokens) =>
            h.Items["session"] is true ? Results.Ok(new { token = tokens.Issue(h.Current().Id) }) : Results.NoContent());
    }
}
