using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
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
    public bool Microsoft => !string.IsNullOrWhiteSpace(TenantId) && !string.IsNullOrWhiteSpace(ClientId);
    /// <summary>The permission the web app and the Widget ask Microsoft for: "use VerbaFlow as me".</summary>
    public string Scope => $"api://{ClientId}/access_as_user";
    /// <summary>The name of the app role that makes someone a VerbaFlow administrator, given in Microsoft Entra ID.</summary>
    public const string AdminRole = "Admin";

    public static SignInOptions From(IConfiguration config, IHostEnvironment env) => new()
    {
        TenantId = config["Entra:TenantId"]?.Trim(),
        ClientId = config["Entra:ClientId"]?.Trim(),
        TestUsers = env.IsDevelopment() && !string.Equals(config["SignIn:TestUsers"], "false", StringComparison.OrdinalIgnoreCase),
    };

    public void AddTo(IServiceCollection services)
    {
        services.AddSingleton(this);
        services.AddSingleton<EntraUsers>();
        if (!Microsoft) return;
        services.AddAuthentication().AddJwtBearer(o =>
        {
            o.Authority = $"https://login.microsoftonline.com/{TenantId}/v2.0";
            o.MapInboundClaims = false; // keep Microsoft's own claim names: oid, tid, scp, roles, name, preferred_username
            o.TokenValidationParameters.ValidAudiences = [ClientId!, $"api://{ClientId}"];
            // Version 2 and version 1 tokens name the issuer differently; only this organisation's directory is accepted.
            o.TokenValidationParameters.ValidIssuers = [$"https://login.microsoftonline.com/{TenantId}/v2.0", $"https://sts.windows.net/{TenantId}/"];
        });
    }

    /// <summary>What the web app and the Widget need to know to sign in. No secrets: these are public identifiers.</summary>
    public object Describe() => new
    {
        microsoft = Microsoft ? new { tenantId = TenantId, clientId = ClientId, scope = Scope, authority = $"https://login.microsoftonline.com/{TenantId}" } : null,
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
        if (options.Microsoft && http.Request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var result = await http.AuthenticateAsync(JwtBearerDefaults.AuthenticationScheme);
            if (!result.Succeeded || Identity(result.Principal!, options) is not { } who)
                return Results.Json(new { error = "Your sign-in has run out or is not for this organisation. Sign in again." }, statusCode: 401);
            user = await http.RequestServices.GetRequiredService<EntraUsers>().ResolveAsync(who);
        }
        else if (options.TestUsers)
        {
            var key = http.Request.Headers["X-Dev-User"].ToString();
            user = key.Length == 0 ? null : DevUsers.Seed.FirstOrDefault(u => u.Email.StartsWith(key + "@", StringComparison.OrdinalIgnoreCase));
        }
        if (user is null) return Results.Json(new { error = options.Microsoft ? "Sign in with your Microsoft work account." : "Choose a user to sign in as." }, statusCode: 401);
        http.Items["user"] = user;
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
        return new EntraIdentity(oid, Get("name"), Get("preferred_username") ?? Get("upn") ?? Get("email") ?? Get("unique_name"), admin);
    }

    public static User Current(this HttpContext http) => (User)http.Items["user"]!;
}
