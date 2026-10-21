using VerbaFlow.Core.Domain;
using VerbaFlow.Infrastructure.Services;

namespace VerbaFlow.Api;

/// <summary>
/// DEVELOPMENT STAND-IN for Microsoft Entra ID sign-in. The caller names a seeded user in the X-Dev-User header.
/// Only available in Development (see SignInOptions.TestUsers); everywhere else people sign in with Microsoft.
/// </summary>
public static class DevUsers
{
    public static readonly User[] Seed =
    [
        new(Guid.Parse("00000000-0000-0000-0000-0000000000a1"), "Alice Author", "alice@example.test", false, Capability.None),
        new(Guid.Parse("00000000-0000-0000-0000-0000000000b2"), "Bob Assignee", "bob@example.test", false, Capability.None),
        new(Guid.Parse("00000000-0000-0000-0000-0000000000c3"), "Carol Admin", "carol@example.test", true, Capability.None),
        new(Guid.Parse("00000000-0000-0000-0000-0000000000d4"), "Dave Admin", "dave@example.test", true, Capability.None),
        // Stands in for VerbaFlow's own support staff, who are not customers and are not administrators of any customer.
        new(Guid.Parse("00000000-0000-0000-0000-0000000000e5"), "Sam Support", "sam@example.test", false, Capability.None, null, true),
    ];

    public static async Task SeedAsync(Stores stores)
    {
        foreach (var u in Seed) await stores.Users.UpsertAsync(u.Id, u);
    }
}
