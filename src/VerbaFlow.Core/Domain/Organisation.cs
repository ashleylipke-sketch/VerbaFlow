namespace VerbaFlow.Core.Domain;

/// <summary>
/// A customer organisation using VerbaFlow. Each has its own people, recordings, administrators, settings (vocabulary,
/// saved text, letterhead, minutes templates, retention, departments, Widget rules) and plan, kept apart from every other.
/// <see cref="TenantId"/> is its Microsoft Entra directory: a work account from that directory signs in as one of its people.
/// The first organisation (the one VerbaFlow was set up for, directory Entra:TenantId) is not stored: it is
/// <see cref="Organisations.Default"/>, and everything made before there were several organisations belongs to it.
/// Organisations are added by VerbaFlow support.
/// </summary>
public sealed record Organisation(Guid Id, string Name, string? TenantId, Guid CreatedBy, DateTimeOffset CreatedAt,
    Guid? ChangedBy = null, DateTimeOffset? ChangedAt = null);

public static class Organisations
{
    /// <summary>The first organisation. Its people, recordings and settings carry no organisation id (null), as before.</summary>
    public static readonly Guid Default = BillingAccounts.Organisation;

    /// <summary>The key of an organisation's one-of-a-kind settings row (letterhead, retention rules, Widget rules): the
    /// first organisation keeps its old fixed key, others use their own id.</summary>
    public static Guid Key(Guid? organisationId, Guid defaultKey) => organisationId is { } id && id != Default ? id : defaultKey;

    /// <summary>Stored form of an organisation id: null for the first organisation.</summary>
    public static Guid? Stored(Guid? organisationId) => organisationId is { } id && id != Default ? id : null;

    /// <summary>Audit details with ("organisation", id) added for any organisation but the first, so History stays as it was there.</summary>
    public static (string, object?)[] With((string, object?)[] details, Guid? organisationId) =>
        Stored(organisationId) is { } id ? [.. details, ("organisation", id)] : details;
}
