using VerbaFlow.Core.Audit;
using VerbaFlow.Core.Domain;

namespace VerbaFlow.Infrastructure.Services;

public sealed record DepartmentMember(Guid Id, string Name, string How);
public sealed record DepartmentView(Guid Id, string Name, string? GroupId, IReadOnlyList<DepartmentMember> Members, bool Mine,
    IReadOnlyList<PersonOption> Admins, bool IAdminister);
public sealed record UnlinkedGroup(string GroupId, int People, IReadOnlyList<string> Names);
public sealed record PersonOption(Guid Id, string Name);
public sealed record DepartmentsOverview(IReadOnlyList<DepartmentView> Departments, IReadOnlyList<string> Mine, bool CanChange,
    IReadOnlyList<UnlinkedGroup> GroupsNotLinked, IReadOnlyList<PersonOption> AddableByHand, int PeopleWithTooManyGroups, bool AnyGroupsSeen,
    IReadOnlyList<PersonOption> People, IReadOnlyList<string> IAdminister);

/// <summary>
/// Departments: everyone can see them; only administrators create, rename, link or remove them. Membership comes from the
/// Microsoft group linked to a department (read from each person's sign-in), or by hand for people who do not sign in with
/// Microsoft (the test users on the owner's computer). People in the same department can open each other's recordings.
/// </summary>
public sealed class DepartmentService(Stores stores, TimeProvider clock)
{
    private const string Product = "platform";
    public const int MaxDepartments = 200;

    /// <summary>
    /// The person with <see cref="User.Colleagues"/> filled in (everyone who shares a department with them) and
    /// <see cref="User.Leads"/> (the members of the departments they are a department admin of).
    /// </summary>
    public async Task<User> WithColleaguesAsync(User user)
    {
        var all = await stores.Departments.ListAsync();
        var departments = all.Where(d => d.Has(user)).ToList();
        var led = user.IsSupport ? [] : all.Where(d => d.Leads.Contains(user.Id)).ToList();
        if (departments.Count == 0 && led.Count == 0)
            return user.Colleagues.Count == 0 && user.Leads.Count == 0 ? user : user with { Colleagues = new HashSet<Guid>(), Leads = new HashSet<Guid>() };
        var users = (await stores.Users.ListAsync()).Where(u => u.Id != user.Id && !u.IsSupport).ToList();
        return user with
        {
            Colleagues = users.Where(u => departments.Any(d => d.Has(u))).Select(u => u.Id).ToHashSet(),
            Leads = users.Where(u => led.Any(d => d.Has(u))).Select(u => u.Id).ToHashSet(),
        };
    }

    public async Task<DepartmentsOverview> OverviewAsync(User actor)
    {
        var departments = await stores.Departments.ListAsync();
        var users = (await stores.Users.ListAsync()).Where(u => !u.IsSupport).ToList();
        var views = departments.OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).Select(d => new DepartmentView(d.Id, d.Name,
            actor.IsAdmin ? d.GroupId : null,
            users.Where(d.Has).OrderBy(u => u.Name).Select(u => new DepartmentMember(u.Id, u.Name, d.HandMembers.Contains(u.Id) ? "by hand" : "Microsoft group")).ToList(),
            d.Has(actor),
            d.Leads.Select(id => new PersonOption(id, users.FirstOrDefault(u => u.Id == id)?.Name ?? "Someone no longer here")).OrderBy(p => p.Name).ToList(),
            d.Leads.Contains(actor.Id))).ToList();
        var linked = departments.Where(d => d.GroupId is not null).Select(d => d.GroupId!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unlinked = !actor.IsAdmin ? [] : users
            .SelectMany(u => (u.GroupIds ?? []).Where(g => g != EntraUsers.TooManyGroups && !linked.Contains(g)).Select(g => (Group: g, User: u)))
            .GroupBy(x => x.Group, StringComparer.OrdinalIgnoreCase)
            .Select(g => new UnlinkedGroup(g.Key, g.Count(), g.Select(x => x.User.Name).Order().Take(8).ToList()))
            .OrderByDescending(g => g.People).ThenBy(g => g.GroupId).Take(50).ToList();
        var byHand = !actor.IsAdmin ? [] : users.Where(u => u.EntraObjectId is null).OrderBy(u => u.Name).Select(u => new PersonOption(u.Id, u.Name)).ToList();
        return new DepartmentsOverview(views, views.Where(v => v.Mine).Select(v => v.Name).ToList(), actor.IsAdmin, unlinked, byHand,
            actor.IsAdmin ? users.Count(u => u.GroupIds?.Contains(EntraUsers.TooManyGroups) == true) : 0,
            users.Any(u => u.GroupIds is { Count: > 0 }),
            !actor.IsAdmin ? [] : users.OrderBy(u => u.Name).Select(u => new PersonOption(u.Id, u.Name)).ToList(),
            views.Where(v => v.IAdminister).Select(v => v.Name).ToList());
    }

    public async Task<Department> CreateAsync(User actor, string? name, string? groupId)
    {
        RequireAdmin(actor);
        var clean = Department.CleanName(name);
        var group = Department.CleanGroupId(groupId);
        var all = await stores.Departments.ListAsync();
        if (all.Count >= MaxDepartments) throw new DomainException($"There is no room for more departments ({MaxDepartments}).");
        CheckUnique(all, null, clean, group);
        var d = new Department(Guid.NewGuid(), clean, group, [], clock.GetUtcNow());
        await stores.Departments.UpsertAsync(d.Id, d);
        await Audit(actor, "department.created", ("department", d.Id), ("name", clean), ("microsoftGroup", group));
        return d;
    }

    public async Task<Department> ChangeAsync(User actor, Guid id, string? name, string? groupId)
    {
        RequireAdmin(actor);
        var d = await Require(id);
        var clean = Department.CleanName(name);
        var group = Department.CleanGroupId(groupId);
        CheckUnique(await stores.Departments.ListAsync(), id, clean, group);
        var changed = d with { Name = clean, GroupId = group };
        await stores.Departments.UpsertAsync(id, changed);
        await Audit(actor, "department.changed", ("department", id), ("name", clean), ("previousName", d.Name == clean ? null : d.Name),
            ("microsoftGroup", group), ("previousMicrosoftGroup", d.GroupId == group ? null : d.GroupId ?? "none"));
        return changed;
    }

    public async Task RemoveAsync(User actor, Guid id)
    {
        RequireAdmin(actor);
        var d = await Require(id);
        await stores.Departments.DeleteAsync(id);
        await Audit(actor, "department.removed", ("department", id), ("name", d.Name));
    }

    /// <summary>Adds or takes off someone by hand. Only for people who do not sign in with Microsoft: the others come from the Microsoft group.</summary>
    public async Task<Department> SetHandMemberAsync(User actor, Guid id, Guid userId, bool member)
    {
        RequireAdmin(actor);
        var d = await Require(id);
        var person = await stores.Users.GetAsync(userId) ?? throw new NotFoundException("That person does not exist.");
        if (member && (person.EntraObjectId is not null || person.IsSupport))
            throw new DomainException($"{person.Name} signs in with Microsoft, so their departments come from their Microsoft groups. Add them to the group linked to {d.Name} instead.");
        if (member == d.HandMembers.Contains(userId)) return d;
        var changed = d with { HandMembers = member ? [.. d.HandMembers, userId] : d.HandMembers.Where(m => m != userId).ToList() };
        await stores.Departments.UpsertAsync(id, changed);
        await Audit(actor, member ? "department.member_added" : "department.member_removed", ("department", id), ("name", d.Name), ("person", person.Name));
        return changed;
    }

    /// <summary>
    /// Makes someone a department admin, or stops it. They can then assign and approve the recordings of the department's
    /// members, as an administrator can, but only those; reopening and deleting stay with administrators.
    /// </summary>
    public async Task<Department> SetAdminAsync(User actor, Guid id, Guid userId, bool admin)
    {
        RequireAdmin(actor);
        var d = await Require(id);
        var person = await stores.Users.GetAsync(userId) ?? throw new NotFoundException("That person does not exist.");
        if (admin && person.IsSupport) throw new DomainException("VerbaFlow support cannot be a department admin.");
        if (admin == d.Leads.Contains(userId)) return d;
        if (admin && d.Leads.Count >= Department.MaxAdmins)
            throw new DomainException($"A department can have at most {Department.MaxAdmins} department admins.");
        var changed = d with { Admins = admin ? [.. d.Leads, userId] : d.Leads.Where(m => m != userId).ToList() };
        await stores.Departments.UpsertAsync(id, changed);
        await Audit(actor, admin ? "department.admin_added" : "department.admin_removed", ("department", id), ("name", d.Name), ("person", person.Name));
        return changed;
    }

    private static void CheckUnique(IReadOnlyList<Department> all, Guid? self, string name, string? group)
    {
        if (all.FirstOrDefault(d => d.Id != self && string.Equals(d.Name, name, StringComparison.CurrentCultureIgnoreCase)) is not null)
            throw new DomainException($"There is already a department called {name}.");
        if (group is not null && all.FirstOrDefault(d => d.Id != self && string.Equals(d.GroupId, group, StringComparison.OrdinalIgnoreCase)) is { } other)
            throw new DomainException($"That Microsoft group is already linked to {other.Name}.");
    }

    private async Task<Department> Require(Guid id) =>
        await stores.Departments.GetAsync(id) ?? throw new NotFoundException("That department does not exist.");

    private static void RequireAdmin(User actor)
    {
        if (!actor.IsAdmin) throw new ForbiddenException("Only administrators can change departments.");
    }

    private Task Audit(User actor, string type, params (string, object?)[] details) =>
        stores.Audit.AppendAsync(actor.Id, "admin", Product, null, type, AuditChain.Details(details));
}
