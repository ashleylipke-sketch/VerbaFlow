using VerbaFlow.Core.Audit;
using VerbaFlow.Core.Domain;

namespace VerbaFlow.Infrastructure.Services;

/// <summary>A personal account whose email address also signs in another way, so it can no longer be used (support only).</summary>
public sealed record SharedAddressAccount(Guid Id, string Name, string Email, string SignedInWith, string OtherSignIn, int Recordings);

/// <summary>
/// One email address has one way to sign in (SignInWays.TakenBy). Personal accounts made before that rule, or before the
/// organisation's own account for the same address, are refused at sign-in; VerbaFlow support can remove them here, with their recordings.
/// </summary>
public sealed class AccountService(Stores stores, MeetingService meetings)
{
    private static void RequireSupport(User actor)
    {
        if (!actor.IsSupport) throw new ForbiddenException("Only VerbaFlow support can remove accounts.");
    }

    public async Task<IReadOnlyList<SharedAddressAccount>> SharedAddressesAsync(User actor)
    {
        RequireSupport(actor);
        var users = await stores.Users.ListAsync();
        var items = await stores.Items.ListAsync();
        return users.Where(u => u.Individual && SignInWays.TakenBy(users, u.SignInId ?? "", u.Email, false) is not null)
            .Select(u => new SharedAddressAccount(u.Id, u.Name, u.Email, SignInWays.Of(u), SignInWays.Of(SignInWays.TakenBy(users, u.SignInId ?? "", u.Email, false)!),
                items.Where(i => i.OwnerId == u.Id && i.Individual && i.Status != ItemStatus.Purged).Select(i => i.ChainId).Distinct().Count()))
            .OrderBy(a => a.Email, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Removes such an account: its recordings are deleted (tombstones and History stay), then the account itself.</summary>
    public async Task<int> RemoveAsync(User actor, Guid id)
    {
        RequireSupport(actor);
        var users = await stores.Users.ListAsync();
        var user = users.FirstOrDefault(u => u.Id == id) ?? throw new NotFoundException("That account does not exist.");
        if (!user.Individual || SignInWays.TakenBy(users, user.SignInId ?? "", user.Email, false) is null)
            throw new DomainException("Only a personal account whose email address also signs in another way can be removed here.");
        var deleted = await meetings.DeleteAllOwnedByAsync(actor, user.Id,
            "Personal account removed by VerbaFlow support: its email address signs in another way.");
        await stores.Users.DeleteAsync(user.Id);
        await stores.Audit.AppendAsync(actor.Id, "support", "platform", null, "user.personal_removed",
            AuditChain.Details(("account", user.Id), ("signedInWith", SignInWays.Of(user)), ("recordingsDeleted", deleted)));
        return deleted;
    }
}
