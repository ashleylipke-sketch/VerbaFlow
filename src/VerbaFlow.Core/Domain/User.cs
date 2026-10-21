namespace VerbaFlow.Core.Domain;

/// <summary>
/// A signed-in user. In production identity comes from Microsoft Entra ID (SSO): <see cref="EntraObjectId"/> is the
/// person's stable id in the organisation's directory, and administrator status comes from the VerbaFlow "Admin" app
/// role. Grants are per-capability rights assigned in the app.
/// </summary>
public sealed record User(Guid Id, string Name, string Email, bool IsAdmin, Capability Grants, string? Department = null, bool IsSupport = false,
    string? EntraObjectId = null);
