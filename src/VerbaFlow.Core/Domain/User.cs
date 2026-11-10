namespace VerbaFlow.Core.Domain;

/// <summary>
/// A signed-in user. In production identity comes from Microsoft Entra ID (SSO): <see cref="EntraObjectId"/> is the
/// person's stable id in the organisation's directory, and administrator status comes from the VerbaFlow "Admin" app
/// role. Grants are per-capability rights assigned in the app.
/// <see cref="GroupIds"/> are the Microsoft groups the person's last sign-in said they are in (object ids), used for departments.
/// <see cref="Individual"/>: someone using VerbaFlow on their own (a personal Microsoft or Google account, or a work or school
/// account from another organisation), not as a member of the organisation. They have a space of their own: they see only their
/// own recordings, none of the organisation's people or settings, and the organisation's administrators do not see their recordings.
/// <see cref="OrganisationId"/>: the organisation they belong to (null for the first one, see <see cref="Organisation"/>).
/// <see cref="SignInId"/> is how an individual is recognised at sign-in ("microsoft:&lt;directory&gt;:&lt;id&gt;" or "google:&lt;id&gt;").
/// </summary>
public sealed record User(Guid Id, string Name, string Email, bool IsAdmin, Capability Grants, string? Department = null, bool IsSupport = false,
    string? EntraObjectId = null, IReadOnlyList<string>? GroupIds = null, bool Individual = false, string? SignInId = null,
    Guid? OrganisationId = null)
{
    /// <summary>
    /// The people who share a department with this person, worked out when they sign in for each request (never stored).
    /// They can open each other's recordings.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlySet<Guid> Colleagues { get; init; } = NoOne;
    private static readonly IReadOnlySet<Guid> NoOne = new HashSet<Guid>();

    /// <summary>
    /// The members of the departments this person is a department admin of (not themselves), worked out per request like
    /// <see cref="Colleagues"/>. They can assign and approve those people's recordings.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlySet<Guid> Leads { get; init; } = NoOne;

    public bool Equals(User? other) =>
        other is not null && Id == other.Id && Name == other.Name && Email == other.Email && IsAdmin == other.IsAdmin && Grants == other.Grants
        && Department == other.Department && IsSupport == other.IsSupport && EntraObjectId == other.EntraObjectId
        && Individual == other.Individual && SignInId == other.SignInId && OrganisationId == other.OrganisationId
        && (GroupIds ?? []).SequenceEqual(other.GroupIds ?? []) && Colleagues.SetEquals(other.Colleagues) && Leads.SetEquals(other.Leads);

    /// <summary>
    /// Whose lists and settings this person uses: their organisation's (null for the first organisation: vocabulary, saved text...),
    /// or their own id when they have a personal account.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public Guid? Space => Individual ? Id : Organisations.Stored(OrganisationId);

    /// <summary>The organisation this person belongs to (<see cref="Organisations.Default"/> for the first one), or null for a personal account.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public Guid? Org => Individual ? null : OrganisationId ?? Organisations.Default;

    /// <summary>Whether this person and that one are in the same organisation (never true for a personal account).</summary>
    public bool SameOrganisation(User other) => Org is { } o && other.Org == o;
    /// <summary>Whether this item belongs to this person's organisation (never true for a personal account's).</summary>
    public bool SameOrganisation(Item item) => Org is { } o && item.Org == o;

    public override int GetHashCode() => HashCode.Combine(Id, Name, Email, IsAdmin, EntraObjectId);
}

/// <summary>How people sign in, in plain words, and the rule that one email address has only one way to sign in.</summary>
public static class SignInWays
{
    /// <summary>Microsoft's directory for personal Microsoft accounts (Outlook.com, Hotmail, Live).</summary>
    public const string MicrosoftPersonalTenant = "9188040d-6c67-4c5b-b112-36a304b66dad";

    /// <summary>How this person signs in (Your account page).</summary>
    public static string Of(User u)
    {
        var id = u.SignInId ?? "";
        if (id.StartsWith("google:", StringComparison.Ordinal)) return "Google account";
        if (id.StartsWith("email:", StringComparison.Ordinal)) return "Email address (a code sent by email)";
        if (id.StartsWith("microsoft:", StringComparison.Ordinal))
            return id.StartsWith($"microsoft:{MicrosoftPersonalTenant}:", StringComparison.OrdinalIgnoreCase)
                ? "Personal Microsoft account" : "Microsoft work or school account from another organisation";
        if (u.EntraObjectId is not null) return "Microsoft work or school account (this organisation's)";
        return "Test user (only on a computer set up for testing)";
    }

    /// <summary>The same way, to finish "sign in with …".</summary>
    public static string With(User u)
    {
        var id = u.SignInId ?? "";
        if (id.StartsWith("google:", StringComparison.Ordinal)) return "your Google account";
        if (id.StartsWith("email:", StringComparison.Ordinal)) return "a code sent to your email address";
        if (id.StartsWith("microsoft:", StringComparison.Ordinal))
            return id.StartsWith($"microsoft:{MicrosoftPersonalTenant}:", StringComparison.OrdinalIgnoreCase)
                ? "your personal Microsoft account" : "your Microsoft work or school account";
        if (u.EntraObjectId is not null) return "your organisation's Microsoft account (on the sign-in page, choose \"Sign in with this organisation's Microsoft account\")";
        return "the test user";
    }

    /// <summary>Whether two email addresses are the same one (capitals and spaces ignored; an empty address matches nothing).</summary>
    public static bool SameAddress(string? a, string? b) =>
        !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b) && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Someone else who already signs in with this email address another way, or null. One email address has one sign-in:
    /// an organisation's own account always keeps it; a personal account keeps it only if it was there first.
    /// </summary>
    /// <param name="signInId">The personal sign-in being used (null for an organisation member, who is never refused).</param>
    /// <param name="isNew">Whether this personal sign-in has no VerbaFlow account yet.</param>
    public static User? TakenBy(IEnumerable<User> users, string? signInId, string? email, bool isNew) =>
        signInId is null ? null : users.FirstOrDefault(u => !u.IsSupport && u.SignInId != signInId && SameAddress(u.Email, email)
            && (isNew || !u.Individual));

    /// <summary>The plain sentence for someone refused because their address already signs in another way.</summary>
    public static string Refusal(string email, User other) =>
        $"{email.Trim()} already signs in to VerbaFlow with {With(other)}. One email address can have only one way to sign in, so sign in that way.";
}
