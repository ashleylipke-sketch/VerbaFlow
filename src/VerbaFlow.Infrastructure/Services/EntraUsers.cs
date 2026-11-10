using System.Collections.Concurrent;
using VerbaFlow.Core.Audit;
using VerbaFlow.Core.Domain;

namespace VerbaFlow.Infrastructure.Services;

/// <summary>Who a Microsoft sign-in says the person is: their directory id, name, email and whether they hold the Admin role.</summary>
/// <param name="Groups">The Microsoft groups the sign-in lists (object ids, sorted, comma separated), <see cref="EntraUsers.TooManyGroups"/>
/// when the person is in too many for the sign-in to list, or empty.</param>
/// <param name="OrganisationId">The organisation whose directory it is (stored form: null for the first one).</param>
public sealed record EntraIdentity(string ObjectId, string? Name, string? Email, bool IsAdmin, string Groups = "", Guid? OrganisationId = null)
{
    public IReadOnlyList<string> GroupList => Groups.Length == 0 ? [] : Groups.Split(',');
}

/// <summary>
/// Someone signing in with a personal account (a personal Microsoft account, a work or school account from another
/// organisation, or a Google account). <paramref name="SignInId"/> is stable for that account ("microsoft:&lt;directory&gt;:&lt;id&gt;",
/// "google:&lt;id&gt;"); <paramref name="Kind"/> names the kind of account for History.
/// </summary>
public sealed record PersonalIdentity(string SignInId, string? Name, string? Email, string Kind);

/// <summary>
/// Turns a Microsoft Entra ID sign-in into a VerbaFlow user. The first sign-in creates the user; later sign-ins keep the
/// name, email and administrator status in step with Microsoft 365, where the organisation manages them. VerbaFlow
/// support access never comes from a customer's directory, so a Microsoft sign-in is never a support user.
/// </summary>
public sealed class EntraUsers(Stores stores, TimeProvider clock)
{
    private const string Product = "platform";
    /// <summary>Kept in <see cref="User.GroupIds"/> when Microsoft said the person is in too many groups to list in the sign-in.</summary>
    public const string TooManyGroups = "too-many";
    /// <summary>How long a looked-up user is trusted before the directory details are compared again.</summary>
    public static readonly TimeSpan Remember = TimeSpan.FromMinutes(5);
    private readonly ConcurrentDictionary<string, (User User, EntraIdentity Seen, DateTimeOffset At)> _known = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<User> ResolveAsync(EntraIdentity who)
    {
        if (string.IsNullOrWhiteSpace(who.ObjectId)) throw new ForbiddenException("Your Microsoft sign-in did not say who you are. Sign in again.");
        var now = clock.GetUtcNow();
        var key = $"{who.OrganisationId}|{who.ObjectId}";
        if (_known.TryGetValue(key, out var hit) && hit.Seen == who && now - hit.At < Remember) return hit.User;
        await _gate.WaitAsync();
        try
        {
            var name = Clean(who.Name, 200) is { Length: > 0 } n ? n : Clean(who.Email, 200) is { Length: > 0 } e ? e : "VerbaFlow user";
            var email = Clean(who.Email, 320) ?? "";
            var existing = (await stores.Users.ListAsync()).FirstOrDefault(u => u.EntraObjectId == who.ObjectId && u.OrganisationId == who.OrganisationId && !u.Individual);
            User user;
            if (existing is null)
            {
                user = new User(Guid.NewGuid(), name, email, who.IsAdmin, Capability.None, EntraObjectId: who.ObjectId, GroupIds: who.GroupList,
                    OrganisationId: who.OrganisationId);
                await stores.Users.UpsertAsync(user.Id, user);
                await Audit(user, "user.first_sign_in", Organisations.With([("isAdmin", user.IsAdmin)], user.Org));
            }
            else
            {
                user = existing with { Name = name, Email = email, IsAdmin = who.IsAdmin, IsSupport = false, GroupIds = who.GroupList };
                if (user != existing)
                {
                    await stores.Users.UpsertAsync(user.Id, user);
                    if (user.IsAdmin != existing.IsAdmin) await Audit(user, "user.admin_changed", ("isAdmin", user.IsAdmin));
                    if (!(existing.GroupIds ?? []).SequenceEqual(user.GroupIds ?? []))
                        await Audit(user, "user.groups_changed", ("microsoftGroups", who.Groups == TooManyGroups ? "too many to list" : user.GroupIds!.Count.ToString()));
                }
            }
            _known[key] = (user, who, now);
            return user;
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// The VerbaFlow user for a personal account: made on the first sign-in, name and email kept in step afterwards. Never an
    /// administrator or VerbaFlow support, in no department, with a space of their own (User.Individual).
    /// </summary>
    public async Task<User> ResolvePersonalAsync(PersonalIdentity who)
    {
        if (string.IsNullOrWhiteSpace(who.SignInId)) throw new ForbiddenException("Your sign-in did not say who you are. Sign in again.");
        var now = clock.GetUtcNow();
        var key = "personal|" + who.SignInId;
        var seen = new EntraIdentity(key, who.Name, who.Email, false);
        if (_known.TryGetValue(key, out var hit) && hit.Seen == seen && now - hit.At < Remember) return hit.User;
        await _gate.WaitAsync();
        try
        {
            var name = Clean(who.Name, 200) is { Length: > 0 } n ? n : Clean(who.Email, 200) is { Length: > 0 } e ? e : "VerbaFlow user";
            var email = Clean(who.Email, 320) ?? "";
            var all = await stores.Users.ListAsync();
            var existing = all.FirstOrDefault(u => u.SignInId == who.SignInId);
            // One email address has one way to sign in (see SignInWays.TakenBy).
            if (SignInWays.TakenBy(all, who.SignInId, existing?.Email is { Length: > 0 } had ? had : email, existing is null) is { } other)
                throw new ForbiddenException(SignInWays.Refusal(existing?.Email is { Length: > 0 } h ? h : email, other));
            User user;
            if (existing is null)
            {
                user = new User(Guid.NewGuid(), name, email, false, Capability.None, Individual: true, SignInId: who.SignInId);
                await stores.Users.UpsertAsync(user.Id, user);
                await Audit(user, "user.first_sign_in", ("account", who.Kind), ("personal", true));
            }
            else
            {
                // An emailed-code sign-in names nobody: keep the name the person gave before.
                if (Clean(who.Name, 200) is null && who.SignInId.StartsWith("email:", StringComparison.Ordinal)) name = existing.Name;
                user = existing with { Name = name, Email = email, IsAdmin = false, IsSupport = false, Individual = true, Grants = Capability.None };
                if (user != existing) await stores.Users.UpsertAsync(user.Id, user);
            }
            _known[key] = (user, seen, now);
            return user;
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// The plain sentence refusing a personal account whose email address now belongs to someone else's sign-in (for example the
    /// organisation's own Microsoft account, added after), or null when it may sign in. Used for VerbaFlow's own sign-ins, which
    /// do not pass through <see cref="ResolvePersonalAsync"/> again.
    /// </summary>
    public async Task<string?> RefusalForAsync(User user) =>
        user.Individual && SignInWays.TakenBy(await stores.Users.ListAsync(), user.SignInId ?? "", user.Email, false) is { } other
            ? SignInWays.Refusal(user.Email, other) : null;

    /// <summary>The plain sentence refusing a new sign-in with this address and way (before an email code is even sent), or null.</summary>
    public async Task<string?> RefusalForAsync(string signInId, string email)
    {
        var all = await stores.Users.ListAsync();
        var existing = all.FirstOrDefault(u => u.SignInId == signInId);
        return SignInWays.TakenBy(all, signInId, email, existing is null) is { } other ? SignInWays.Refusal(email, other) : null;
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
