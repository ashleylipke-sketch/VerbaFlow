namespace VerbaFlow.Core.Domain;

/// <summary>
/// A team in the organisation, such as Legal or Finance. People in the same department can open each other's recordings
/// (read, listen and, once approved, export) but cannot change, assign, approve or delete them.
/// Members come from a Microsoft group (its object id, <see cref="GroupId"/>), read from each person's Microsoft sign-in.
/// <see cref="HandMembers"/> holds people added by hand, only for those who do not sign in with Microsoft (the test users).
/// <see cref="Admins"/> are the department's own administrators, picked by an administrator: they can assign and approve the
/// recordings of the department's members (not reopen or delete them), and do not need to be members themselves.
/// </summary>
public sealed record Department(Guid Id, string Name, string? GroupId, IReadOnlyList<Guid> HandMembers, DateTimeOffset CreatedAt,
    IReadOnlyList<Guid>? Admins = null, Guid? OrganisationId = null)
{
    public const int MaxAdmins = 10;

    /// <summary>Whether the department is in this person's organisation (personal accounts have no departments).</summary>
    public bool InOrganisationOf(User user) => !user.Individual && OrganisationId == Organisations.Stored(user.Org);

    /// <summary>The department admins (none on departments made before they existed).</summary>
    public IReadOnlyList<Guid> Leads => Admins ?? [];

    public const int MaxName = 80;

    public static string CleanName(string? name)
    {
        var n = new string((name ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (n.Length == 0) throw new DomainException("Give the department a name.");
        if (n.Length > MaxName) throw new DomainException($"A department name can be at most {MaxName} characters.");
        return n;
    }

    /// <summary>A Microsoft group's object id (a GUID as Microsoft shows it), or null when left empty.</summary>
    public static string? CleanGroupId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        if (!Guid.TryParse(id.Trim(), out var g)) throw new DomainException("The Microsoft group's Object ID looks like 1a2b3c4d-1234-5678-9abc-1a2b3c4d5e6f. Copy it from the group's Overview page in Microsoft Entra.");
        return g.ToString("D");
    }

    public bool Has(User user) =>
        HandMembers.Contains(user.Id) || (GroupId is not null && user.GroupIds is { } g && g.Contains(GroupId, StringComparer.OrdinalIgnoreCase));
}
