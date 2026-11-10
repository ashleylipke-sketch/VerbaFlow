using VerbaFlow.Core.Domain;
using VerbaFlow.Infrastructure.Services;

namespace VerbaFlow.Api;

/// <summary>
/// DEVELOPMENT STAND-IN for Microsoft Entra ID sign-in. The caller names a seeded user in the X-Dev-User header.
/// Only available in Development (see SignInOptions.TestUsers); everywhere else people sign in with Microsoft.
/// </summary>
public static class DevUsers
{
    /// <summary>A second, made-up organisation for trying several organisations: Olivia (its administrator) and Oscar.</summary>
    public static readonly Organisation Northwind = new(Guid.Parse("00000000-0000-0000-0000-00000000f0f0"), "Northwind (test)", null,
        Guid.Parse("00000000-0000-0000-0000-0000000000e5"), new DateTimeOffset(2026, 11, 10, 0, 0, 0, TimeSpan.Zero));

    public static readonly User[] Seed =
    [
        new(Guid.Parse("00000000-0000-0000-0000-0000000000a1"), "Alice Author", "alice@example.test", false, Capability.None),
        new(Guid.Parse("00000000-0000-0000-0000-0000000000b2"), "Bob Assignee", "bob@example.test", false, Capability.None),
        new(Guid.Parse("00000000-0000-0000-0000-0000000000c3"), "Carol Admin", "carol@example.test", true, Capability.None),
        new(Guid.Parse("00000000-0000-0000-0000-0000000000d4"), "Dave Admin", "dave@example.test", true, Capability.None),
        // Stands in for VerbaFlow's own support staff, who are not customers and are not administrators of any customer.
        new(Guid.Parse("00000000-0000-0000-0000-0000000000e5"), "Sam Support", "sam@example.test", false, Capability.None, null, true),
        new(Guid.Parse("00000000-0000-0000-0000-0000000000f6"), "Olivia Admin (Northwind)", "olivia@example.test", true, Capability.None, OrganisationId: Northwind.Id),
        new(Guid.Parse("00000000-0000-0000-0000-0000000000f7"), "Oscar (Northwind)", "oscar@example.test", false, Capability.None, OrganisationId: Northwind.Id),
    ];

    public static async Task SeedAsync(Stores stores, OrganisationService organisations)
    {
        await organisations.EnsureAsync(Northwind);
        foreach (var u in Seed) await stores.Users.UpsertAsync(u.Id, u);
    }
}
