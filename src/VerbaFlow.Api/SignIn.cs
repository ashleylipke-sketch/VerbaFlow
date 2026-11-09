using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using VerbaFlow.Core.Domain;
using VerbaFlow.Infrastructure.Services;

namespace VerbaFlow.Api;

/// <summary>
/// How people sign in. With <c>Entra:TenantId</c> and <c>Entra:ClientId</c> set, people sign in with their Microsoft work
/// account (Microsoft Entra ID): the web app and the Widget send a Microsoft access token, checked here. The test users
/// (alice, bob, carol...) stay available only while VerbaFlow runs in Development on the owner's computer, and can be
/// switched off there with <c>SignIn:TestUsers=false</c>. They are never available anywhere else.
/// </summary>
public sealed class SignInOptions
{
    public string? TenantId { get; init; }
    public string? ClientId { get; init; }
    public bool TestUsers { get; init; }
    /// <summary>
    /// People without a work account in this organisation may sign in too (<c>Entra:PersonalAccounts=true</c>): personal
    /// Microsoft accounts (Outlook.com, Hotmail, Microsoft 365 Family) and work or school accounts from other organisations.
    /// They get a personal space of their own (User.Individual). Needs the app registration's supported account types set to
    /// "any organisational directory and personal Microsoft accounts".
    /// </summary>
    public bool PersonalAccounts { get; init; }
    /// <summary>Google sign-in for personal accounts (<c>Google:ClientId</c>, a Google Cloud OAuth client id; not secret).</summary>
    public string? GoogleClientId { get; init; }
    /// <summary>
    /// Sign-in with an emailed code for personal accounts (no Google or Microsoft account needed). On whenever personal
    /// accounts are offered, unless <c>SignIn:EmailCodes=false</c>; online only once real email is set up (Email:ConnectionString),
    /// because the stand-in only writes emails to the data folder.
    /// </summary>
    public bool EmailCodes { get; init; }
    public bool Microsoft => !string.IsNullOrWhiteSpace(TenantId) && !string.IsNullOrWhiteSpace(ClientId);
    public bool Google => !string.IsNullOrWhiteSpace(GoogleClientId);
    /// <summary>Microsoft's own tenant id for personal Microsoft accounts.</summary>
    public const string ConsumersTenant = "9188040d-6c67-4c5b-b5f8-a3d2c3c9b6e1";
    /// <summary>The permission the web app and the Widget ask Microsoft for: "use VerbaFlow as me".</summary>
    public string Scope => $"api://{ClientId}/access_as_user";
    /// <summary>The name of the app role that makes someone a VerbaFlow administrator, given in Microsoft Entra ID.</summary>
    public const string AdminRole = "Admin";

    public static SignInOptions From(IConfiguration config, IHostEnvironment env) => new()
    {
        TenantId = config["Entra:TenantId"]?.Trim(),
        ClientId = config["Entra:ClientId"]?.Trim(),
        TestUsers = env.IsDevelopment() && !string.Equals(config["SignIn:TestUsers"], "false", StringComparison.OrdinalIgnoreCase),
        PersonalAccounts = string.Equals(config["Entra:PersonalAccounts"], "true", StringComparison.OrdinalIgnoreCase),
        GoogleClientId = config["Google:ClientId"]?.Trim() is { Length: > 0 } g ? g : null,
        EmailCodes = (string.Equals(config["Entra:PersonalAccounts"], "true", StringComparison.OrdinalIgnoreCase) || !string.IsNullOrWhiteSpace(config["Google:ClientId"]))
            && !string.Equals(config["SignIn:EmailCodes"], "false", StringComparison.OrdinalIgnoreCase)
            && (env.IsDevelopment() || !string.IsNullOrWhiteSpace(config["Email:ConnectionString"])),
    };

    public void AddTo(IServiceCollection services)
    {
        services.AddSingleton(this);
        services.AddSingleton<EntraUsers>();
        services.AddSingleton<SessionTokens>();
        services.AddSingleton<EmailCodes>();
        if (Google) services.AddSingleton(new GoogleSignIn(GoogleClientId!));
        if (!Microsoft) return;
        services.AddAuthentication().AddJwtBearer(o =>
        {
            // With personal accounts the keys come from Microsoft's shared endpoint, which signs every directory's sign-ins.
            o.Authority = PersonalAccounts ? "https://login.microsoftonline.com/common/v2.0" : $"https://login.microsoftonline.com/{TenantId}/v2.0";
            o.MapInboundClaims = false; // keep Microsoft's own claim names: oid, tid, scp, roles, name, preferred_username
            o.TokenValidationParameters.ValidAudiences = [ClientId!, $"api://{ClientId}"];
            // Version 2 and version 1 tokens name the issuer differently; only this organisation's directory is accepted.
            o.TokenValidationParameters.ValidIssuers = [$"https://login.microsoftonline.com/{TenantId}/v2.0", $"https://sts.windows.net/{TenantId}/"];
            // With personal accounts, any directory's sign-in is accepted, as long as the issuer is that directory's own.
            if (PersonalAccounts) o.TokenValidationParameters.IssuerValidator = (issuer, token, p) =>
                p.ValidIssuers!.Contains(issuer) || token is JsonWebToken jwt && jwt.TryGetPayloadValue<string>("tid", out var tid)
                    && Guid.TryParse(tid, out _) && issuer == $"https://login.microsoftonline.com/{tid}/v2.0"
                    ? issuer : throw new SecurityTokenInvalidIssuerException("The sign-in is not from a Microsoft directory.");
        });
    }

    /// <summary>What the web app and the Widget need to know to sign in. No secrets: these are public identifiers.</summary>
    public object Describe() => new
    {
        microsoft = Microsoft ? new { tenantId = TenantId, clientId = ClientId, scope = Scope,
            authority = PersonalAccounts ? "https://login.microsoftonline.com/common" : $"https://login.microsoftonline.com/{TenantId}" } : null,
        google = Google ? new { clientId = GoogleClientId } : null,
        personalAccounts = PersonalAccounts || Google || EmailCodes,
        email = EmailCodes,
        testUsers = TestUsers,
    };
}

public static class SignIn
{
    /// <summary>Every /api call: a Microsoft access token, or (on the owner's computer only) a test user's name.</summary>
    public static async ValueTask<object?> RequireUser(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var http = ctx.HttpContext;
        var options = http.RequestServices.GetRequiredService<SignInOptions>();
        User? user = null;
        var authorization = http.Request.Headers.Authorization.ToString();
        if (authorization.StartsWith("Bearer " + SessionTokens.Prefix, StringComparison.OrdinalIgnoreCase))
        {
            // VerbaFlow's own sign-in (Google accounts): only for a personal account that still exists.
            var id = http.RequestServices.GetRequiredService<SessionTokens>().Read(authorization["Bearer ".Length..].Trim());
            user = id is { } uid ? await http.RequestServices.GetRequiredService<Stores>().Users.GetAsync(uid) : null;
            if (user is not { Individual: true }) return Results.Json(new { error = "Your sign-in has run out. Sign in again." }, statusCode: 401);
            http.Items["session"] = true;
        }
        else if (options.Microsoft && authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var result = await http.AuthenticateAsync(JwtBearerDefaults.AuthenticationScheme);
            var users = http.RequestServices.GetRequiredService<EntraUsers>();
            if (result.Succeeded && Identity(result.Principal!, options) is { } who) user = await users.ResolveAsync(who);
            else if (result.Succeeded && PersonalIdentityOf(result.Principal!, options) is { } personal) user = await users.ResolvePersonalAsync(personal);
            else return Results.Json(new { error = "Your sign-in has run out or is not for this organisation. Sign in again." }, statusCode: 401);
        }
        else if (options.TestUsers)
        {
            var key = http.Request.Headers["X-Dev-User"].ToString();
            user = key.Length == 0 ? null : DevUsers.Seed.FirstOrDefault(u => u.Email.StartsWith(key + "@", StringComparison.OrdinalIgnoreCase));
        }
        if (user is null) return Results.Json(new { error = options.Microsoft || options.Google ? "Sign in to VerbaFlow." : "Choose a user to sign in as." }, statusCode: 401);
        http.Items["user"] = await http.RequestServices.GetRequiredService<DepartmentService>().WithColleaguesAsync(user);
        return await next(ctx);
    }

    /// <summary>The person a checked token names, or null when it is not a person using VerbaFlow (for example an app's own token).</summary>
    public static EntraIdentity? Identity(ClaimsPrincipal p, SignInOptions options)
    {
        string? Get(string type) => p.FindFirst(type)?.Value;
        if (!string.Equals(Get("tid"), options.TenantId, StringComparison.OrdinalIgnoreCase)) return null;
        if (!(Get("scp") ?? "").Split(' ').Contains("access_as_user")) return null;
        var oid = Get("oid");
        if (string.IsNullOrWhiteSpace(oid)) return null;
        var admin = p.FindAll("roles").Any(r => r.Value == SignInOptions.AdminRole);
        return new EntraIdentity(oid, Get("name"), Get("preferred_username") ?? Get("upn") ?? Get("email") ?? Get("unique_name"), admin, Groups(p));
    }

    /// <summary>
    /// Someone with a Microsoft account from outside this organisation (a personal Microsoft account, or a work or school account
    /// from another directory), when personal accounts are switched on. Null for this organisation's members and app-only tokens.
    /// Roles and groups from another directory mean nothing here and are ignored.
    /// </summary>
    public static PersonalIdentity? PersonalIdentityOf(ClaimsPrincipal p, SignInOptions options)
    {
        string? Get(string type) => p.FindFirst(type)?.Value;
        var tid = Get("tid");
        if (!options.PersonalAccounts || string.IsNullOrWhiteSpace(tid) || string.Equals(tid, options.TenantId, StringComparison.OrdinalIgnoreCase)) return null;
        if (!(Get("scp") ?? "").Split(' ').Contains("access_as_user")) return null;
        var id = Get("oid") ?? Get("sub");
        if (string.IsNullOrWhiteSpace(id)) return null;
        var consumer = string.Equals(tid, SignInOptions.ConsumersTenant, StringComparison.OrdinalIgnoreCase);
        return new PersonalIdentity($"microsoft:{tid.ToLowerInvariant()}:{id}", Get("name"),
            Get("preferred_username") ?? Get("email") ?? Get("upn"), consumer ? "personal Microsoft account" : "Microsoft account from another organisation");
    }

    /// <summary>
    /// The Microsoft groups the token lists (when the app registration's token configuration adds the groups claim), used for
    /// departments. Someone in too many groups gets a pointer instead of the list ("overage"), recorded as TooManyGroups.
    /// </summary>
    public static string Groups(ClaimsPrincipal p)
    {
        if (p.FindFirst("hasgroups") is not null || (p.FindFirst("_claim_names")?.Value ?? "").Contains("\"groups\"", StringComparison.Ordinal))
            return EntraUsers.TooManyGroups;
        var ids = p.FindAll("groups").Select(c => Guid.TryParse(c.Value, out var g) ? g.ToString("D") : null).Where(g => g is not null)
            .Distinct().Order(StringComparer.Ordinal).Take(500);
        return string.Join(",", ids);
    }

    public static User Current(this HttpContext http) => (User)http.Items["user"]!;
}
