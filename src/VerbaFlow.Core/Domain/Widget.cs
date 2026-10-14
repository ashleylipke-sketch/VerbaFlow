namespace VerbaFlow.Core.Domain;

/// <summary>
/// One use of the Widget: dictating into one box on one website. Widget keeps no text and no audio, only these facts.
/// </summary>
/// <param name="Site">The website's address (host only, for example mail.google.com).</param>
/// <param name="Device">A short browser and system label, for example "Edge on Windows".</param>
/// <param name="LiveId">The live connection in use now; a new one starts each time the person speaks again.</param>
/// <param name="Outcome">accepted, cancelled, or null while still open.</param>
public sealed record WidgetSession(Guid Id, Guid UserId, string Site, string Device, string Languages, DateTimeOffset StartedAt,
    Guid LiveId, int Listens = 1, DateTimeOffset? EndedAt = null, string? Outcome = null, int? SpokenMs = null, int? Words = null, int? Reconnects = null);

/// <summary>Websites where the Widget must not start, set by administrators. A single row.</summary>
public sealed record WidgetPolicy(IReadOnlyList<string> BlockedSites, Guid? ChangedBy = null, DateTimeOffset? ChangedAt = null)
{
    public static readonly Guid RowId = Guid.Parse("00000000-0000-0000-0000-000000000016");
}
