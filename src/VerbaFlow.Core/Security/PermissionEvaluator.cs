using VerbaFlow.Core.Domain;

namespace VerbaFlow.Core.Security;

public readonly record struct Decision(bool Allowed, string Reason)
{
    public static Decision Allow(string why) => new(true, why);
    public static Decision Deny(string why) => new(false, why);
}

/// <summary>
/// Effective permissions, evaluated in a fixed order:
/// 1. Lock: an approved item can only be read or exported, whatever anyone has been granted.
/// 2. Role on this item: the owner (author/importer) and the assignee.
/// 3. Administrator and per-capability grants.
/// 4. Otherwise deny.
/// Audio is never writable in Meeting mode, so there is no capability for it.
/// </summary>
public static class PermissionEvaluator
{
    private const Capability WriteLike = Capability.Write | Capability.Reassign | Capability.Approve | Capability.EditLockedContent;

    public static ItemRole RoleOn(User user, Item item)
    {
        if (item.OwnerId == user.Id) return ItemRole.Owner;
        if (item.AssignedUserId == user.Id) return ItemRole.Assignee;
        return ItemRole.None;
    }

    public static Decision Evaluate(User user, Item item, Capability capability)
    {
        if (item.IsLocked && (capability & WriteLike) != 0)
            return Decision.Deny("The item is approved and locked.");

        var role = RoleOn(user, item);
        switch (capability)
        {
            case Capability.Read when role != ItemRole.None:
                return Decision.Allow($"role:{role}");
            case Capability.Write when role == ItemRole.Owner:
                return Decision.Allow("role:Owner");
            case Capability.Write when role == ItemRole.Assignee && item.Status == ItemStatus.WithAssignee:
                return Decision.Allow("role:Assignee");
            case Capability.Reassign or Capability.Approve when role == ItemRole.Owner:
                return Decision.Allow("role:Owner");
        }

        if (user.IsAdmin && capability is Capability.Read or Capability.Reassign or Capability.Approve or Capability.Export)
            return Decision.Allow("admin");

        if ((user.Grants & capability) == capability && capability != Capability.None)
            return Decision.Allow("grant");

        return Decision.Deny("You do not have permission to do this.");
    }

    public static void Require(User user, Item item, Capability capability)
    {
        var d = Evaluate(user, item, capability);
        if (!d.Allowed) throw new ForbiddenException(d.Reason);
    }

    /// <summary>Whether the user can see the item at all (dashboard visibility).</summary>
    public static bool CanSee(User user, Item item) =>
        user.IsAdmin || RoleOn(user, item) != ItemRole.None;
}
