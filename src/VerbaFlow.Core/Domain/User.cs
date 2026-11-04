namespace VerbaFlow.Core.Domain;

/// <summary>
/// A signed-in user. In production identity comes from Microsoft Entra ID (SSO): <see cref="EntraObjectId"/> is the
/// person's stable id in the organisation's directory, and administrator status comes from the VerbaFlow "Admin" app
/// role. Grants are per-capability rights assigned in the app.
/// <see cref="GroupIds"/> are the Microsoft groups the person's last sign-in said they are in (object ids), used for departments.
/// </summary>
public sealed record User(Guid Id, string Name, string Email, bool IsAdmin, Capability Grants, string? Department = null, bool IsSupport = false,
    string? EntraObjectId = null, IReadOnlyList<string>? GroupIds = null)
{
    /// <summary>
    /// The people who share a department with this person, worked out when they sign in for each request (never stored).
    /// They can open each other's recordings.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlySet<Guid> Colleagues { get; init; } = NoOne;
    private static readonly IReadOnlySet<Guid> NoOne = new HashSet<Guid>();

    public bool Equals(User? other) =>
        other is not null && Id == other.Id && Name == other.Name && Email == other.Email && IsAdmin == other.IsAdmin && Grants == other.Grants
        && Department == other.Department && IsSupport == other.IsSupport && EntraObjectId == other.EntraObjectId
        && (GroupIds ?? []).SequenceEqual(other.GroupIds ?? []) && Colleagues.SetEquals(other.Colleagues);

    public override int GetHashCode() => HashCode.Combine(Id, Name, Email, IsAdmin, EntraObjectId);
}
