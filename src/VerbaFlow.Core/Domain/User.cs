namespace VerbaFlow.Core.Domain;

/// <summary>
/// A signed-in user. In production identity comes from Microsoft Entra ID (SSO);
/// admin status and department come from group membership. Grants are per-capability
/// rights assigned in the app.
/// </summary>
public sealed record User(Guid Id, string Name, string Email, bool IsAdmin, Capability Grants, string? Department = null, bool IsSupport = false);
