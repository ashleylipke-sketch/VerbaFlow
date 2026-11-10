using System.Text.RegularExpressions;
using VerbaFlow.Core.Audit;
using VerbaFlow.Core.Domain;

namespace VerbaFlow.Infrastructure.Services;

public sealed record OrganisationRow(Guid Id, string Name, string? TenantId, bool First, int People, int Admins, string Plan, DateTimeOffset? CreatedAt);
public sealed record OrganisationBody(string? Name, string? TenantId);

/// <summary>
/// The organisations using VerbaFlow. The first one (set up with VerbaFlow, Entra:TenantId) always exists and is not stored;
/// VerbaFlow support adds the others, each with its Microsoft directory so its people's work accounts sign in to it.
/// </summary>
/// <param name="firstName">What the first organisation is called (setting Organisation:Name).</param>
/// <param name="firstTenant">The first organisation's Microsoft directory (Entra:TenantId), if Microsoft sign-in is set up.</param>
public sealed class OrganisationService(Stores stores, TimeProvider clock, string firstName = "Your organisation", string? firstTenant = null)
{
    public const int MaxNameLength = 100;
    private readonly SemaphoreSlim gate = new(1, 1);
    private IReadOnlyList<Organisation>? cached;

    public string FirstName => firstName;

    private async Task<IReadOnlyList<Organisation>> AllAsync() => cached ??= await stores.Organisations.ListAsync();

    /// <summary>The organisation whose Microsoft directory this is: the first one's directory gives null (the first organisation).</summary>
    public async Task<(bool Found, Guid? OrganisationId)> ByTenantAsync(string? tenantId)
    {
        if (string.IsNullOrWhiteSpace(tenantId)) return (false, null);
        if (string.Equals(tenantId, firstTenant, StringComparison.OrdinalIgnoreCase)) return (true, null);
        var o = (await AllAsync()).FirstOrDefault(x => string.Equals(x.TenantId, tenantId, StringComparison.OrdinalIgnoreCase));
        return o is null ? (false, null) : (true, o.Id);
    }

    /// <summary>The Microsoft directory (Tenant ID) of an organisation (null = the first), or null when it has none.</summary>
    public async Task<string?> TenantOfAsync(Guid? organisationId) =>
        organisationId is null || organisationId == Organisations.Default ? firstTenant
            : (await AllAsync()).FirstOrDefault(o => o.Id == organisationId)?.TenantId;

    public async Task<string> NameAsync(Guid? organisationId) =>
        Organisations.Stored(organisationId) is { } id ? (await AllAsync()).FirstOrDefault(o => o.Id == id)?.Name ?? "An organisation" : firstName;

    /// <summary>Every organisation's id, the first one's as <see cref="Organisations.Default"/>.</summary>
    public async Task<IReadOnlyList<Guid>> IdsAsync() => [Organisations.Default, .. (await AllAsync()).Select(o => o.Id)];

    private static void RequireSupport(User actor)
    {
        if (!actor.IsSupport) throw new ForbiddenException("Only VerbaFlow support can see and change organisations.");
    }

    public async Task<IReadOnlyList<OrganisationRow>> ListAsync(User actor, BillingService? billing = null)
    {
        RequireSupport(actor);
        var users = (await stores.Users.ListAsync()).Where(u => !u.Individual && !u.IsSupport).ToList();
        async Task<string> Plan(Guid id) => billing is null ? "" : (await billing.PlanOfAsync(id)).Plan.Name;
        var rows = new List<OrganisationRow>
        {
            new(Organisations.Default, firstName, firstTenant, true, users.Count(u => u.OrganisationId is null),
                users.Count(u => u.OrganisationId is null && u.IsAdmin), await Plan(Organisations.Default), null),
        };
        foreach (var o in (await AllAsync()).OrderBy(o => o.Name, StringComparer.CurrentCultureIgnoreCase))
            rows.Add(new(o.Id, o.Name, o.TenantId, false, users.Count(u => u.OrganisationId == o.Id), users.Count(u => u.OrganisationId == o.Id && u.IsAdmin),
                await Plan(o.Id), o.CreatedAt));
        return rows;
    }

    private async Task<(string Name, string? Tenant)> CheckAsync(OrganisationBody b, Guid? except)
    {
        var name = (b.Name ?? "").Trim();
        if (name.Length == 0) throw new DomainException("Give the organisation a name.");
        if (name.Length > MaxNameLength) throw new DomainException($"The name can be up to {MaxNameLength} characters.");
        var tenant = string.IsNullOrWhiteSpace(b.TenantId) ? null : b.TenantId.Trim().ToLowerInvariant();
        if (tenant is not null && !Guid.TryParse(tenant, out _))
            throw new DomainException("The Microsoft directory id looks like 72f988bf-86f1-41af-91ab-2d7cd011db47 (Microsoft Entra admin centre, Overview, Tenant ID).");
        if (tenant is not null && string.Equals(tenant, firstTenant, StringComparison.OrdinalIgnoreCase))
            throw new DomainException($"That Microsoft directory is already {firstName}'s.");
        var all = await AllAsync();
        if (tenant is not null && all.FirstOrDefault(o => o.Id != except && o.TenantId == tenant) is { } t)
            throw new DomainException($"That Microsoft directory is already {t.Name}'s.");
        if (string.Equals(name, firstName, StringComparison.CurrentCultureIgnoreCase) || all.Any(o => o.Id != except && string.Equals(o.Name, name, StringComparison.CurrentCultureIgnoreCase)))
            throw new DomainException("There is already an organisation with that name.");
        return (name, tenant);
    }

    public async Task<Organisation> CreateAsync(User actor, OrganisationBody b)
    {
        RequireSupport(actor);
        await gate.WaitAsync();
        try
        {
            var (name, tenant) = await CheckAsync(b, null);
            var o = new Organisation(Guid.NewGuid(), name, tenant, actor.Id, clock.GetUtcNow());
            await stores.Organisations.UpsertAsync(o.Id, o);
            cached = null;
            await stores.Audit.AppendAsync(actor.Id, "support", "platform", null, "organisation.created",
                AuditChain.Details(("organisation", o.Id), ("microsoftDirectoryLinked", tenant is not null)));
            return o;
        }
        finally { gate.Release(); }
    }

    public async Task<Organisation> ChangeAsync(User actor, Guid id, OrganisationBody b)
    {
        RequireSupport(actor);
        if (id == Organisations.Default) throw new DomainException($"{firstName} is set up in VerbaFlow's settings (Organisation:Name, Entra:TenantId).");
        await gate.WaitAsync();
        try
        {
            var o = await stores.Organisations.GetAsync(id) ?? throw new NotFoundException("That organisation does not exist.");
            var (name, tenant) = await CheckAsync(b, id);
            var next = o with { Name = name, TenantId = tenant, ChangedBy = actor.Id, ChangedAt = clock.GetUtcNow() };
            await stores.Organisations.UpsertAsync(id, next);
            cached = null;
            await stores.Audit.AppendAsync(actor.Id, "support", "platform", null, "organisation.changed",
                AuditChain.Details(("organisation", id), ("renamed", o.Name != name), ("microsoftDirectoryChanged", o.TenantId != tenant)));
            return next;
        }
        finally { gate.Release(); }
    }

    /// <summary>For tests and test users: an organisation with a fixed id.</summary>
    public async Task EnsureAsync(Organisation o)
    {
        if (await stores.Organisations.GetAsync(o.Id) is null) { await stores.Organisations.UpsertAsync(o.Id, o); cached = null; }
    }
}
