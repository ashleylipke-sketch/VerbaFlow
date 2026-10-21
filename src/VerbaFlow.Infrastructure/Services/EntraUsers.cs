using System.Collections.Concurrent;
using VerbaFlow.Core.Audit;
using VerbaFlow.Core.Domain;

namespace VerbaFlow.Infrastructure.Services;

/// <summary>Who a Microsoft sign-in says the person is: their directory id, name, email and whether they hold the Admin role.</summary>
public sealed record EntraIdentity(string ObjectId, string? Name, string? Email, bool IsAdmin);

/// <summary>
/// Turns a Microsoft Entra ID sign-in into a VerbaFlow user. The first sign-in creates the user; later sign-ins keep the
/// name, email and administrator status in step with Microsoft 365, where the organisation manages them. VerbaFlow
/// support access never comes from a customer's directory, so a Microsoft sign-in is never a support user.
/// </summary>
public sealed class EntraUsers(Stores stores, TimeProvider clock)
{
    private const string Product = "platform";
    /// <summary>How long a looked-up user is trusted before the directory details are compared again.</summary>
    public static readonly TimeSpan Remember = TimeSpan.FromMinutes(5);
    private readonly ConcurrentDictionary<string, (User User, EntraIdentity Seen, DateTimeOffset At)> _known = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<User> ResolveAsync(EntraIdentity who)
    {
        if (string.IsNullOrWhiteSpace(who.ObjectId)) throw new ForbiddenException("Your Microsoft sign-in did not say who you are. Sign in again.");
        var now = clock.GetUtcNow();
        if (_known.TryGetValue(who.ObjectId, out var hit) && hit.Seen == who && now - hit.At < Remember) return hit.User;
        await _gate.WaitAsync();
        try
        {
            var name = Clean(who.Name, 200) is { Length: > 0 } n ? n : Clean(who.Email, 200) is { Length: > 0 } e ? e : "VerbaFlow user";
            var email = Clean(who.Email, 320) ?? "";
            var existing = (await stores.Users.ListAsync()).FirstOrDefault(u => u.EntraObjectId == who.ObjectId);
            User user;
            if (existing is null)
            {
                user = new User(Guid.NewGuid(), name, email, who.IsAdmin, Capability.None, EntraObjectId: who.ObjectId);
                await stores.Users.UpsertAsync(user.Id, user);
                await Audit(user, "user.first_sign_in", ("isAdmin", user.IsAdmin));
            }
            else
            {
                user = existing with { Name = name, Email = email, IsAdmin = who.IsAdmin, IsSupport = false };
                if (user != existing)
                {
                    await stores.Users.UpsertAsync(user.Id, user);
                    if (user.IsAdmin != existing.IsAdmin) await Audit(user, "user.admin_changed", ("isAdmin", user.IsAdmin));
                }
            }
            _known[who.ObjectId] = (user, who, now);
            return user;
        }
        finally { _gate.Release(); }
    }

    private static string? Clean(string? s, int max)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var t = new string(s.Where(c => !char.IsControl(c)).ToArray()).Trim();
        return t.Length > max ? t[..max] : t;
    }

    private Task Audit(User user, string type, params (string, object?)[] details) =>
        stores.Audit.AppendAsync(user.Id, "user", Product, null, type, AuditChain.Details(details));
}
