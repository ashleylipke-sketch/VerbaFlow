using VerbaFlow.Core.Domain;
using VerbaFlow.Infrastructure.Services;

namespace VerbaFlow.Api;

/// <summary>
/// DEVELOPMENT STAND-IN for Microsoft Entra ID sign-in. The caller names a seeded user in the X-Dev-User header.
/// It is never used outside Development; production uses Entra tokens.
/// </summary>
public static class DevUsers
{
    public static readonly User[] Seed =
    [
        new(Guid.Parse("00000000-0000-0000-0000-0000000000a1"), "Alice Author", "alice@example.test", false, Capability.None),
        new(Guid.Parse("00000000-0000-0000-0000-0000000000b2"), "Bob Assignee", "bob@example.test", false, Capability.None),
        new(Guid.Parse("00000000-0000-0000-0000-0000000000c3"), "Carol Admin", "carol@example.test", true, Capability.None),
        new(Guid.Parse("00000000-0000-0000-0000-0000000000d4"), "Dave Admin", "dave@example.test", true, Capability.None),
    ];

    public static async Task SeedAsync(Stores stores)
    {
        foreach (var u in Seed) await stores.Users.UpsertAsync(u.Id, u);
    }

    public static async ValueTask<object?> RequireUser(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var http = ctx.HttpContext;
        var key = http.Request.Headers["X-Dev-User"].ToString();
        var user = Seed.FirstOrDefault(u => u.Email.StartsWith(key + "@", StringComparison.OrdinalIgnoreCase));
        if (user is null) return Results.Json(new { error = "Choose a user to sign in as." }, statusCode: 401);
        http.Items["user"] = user;
        return await next(ctx);
    }

    public static User Current(this HttpContext http) => (User)http.Items["user"]!;
}
