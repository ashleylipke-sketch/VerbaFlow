using VerbaFlow.Core.Domain;
using VerbaFlow.Core.Security;
using VerbaFlow.Infrastructure.Services;

namespace VerbaFlow.Tests.Services;

/// <summary>Several organisations on one VerbaFlow: each has its own people, recordings, administrators, settings and plan.</summary>
public class OrganisationTests
{
    private static readonly User Support = new(Guid.NewGuid(), "Sam Support", "sam@example.test", false, Capability.None, null, true);

    private static async Task<(Organisation Org, User Olivia, User Oscar)> Northwind(Env env)
    {
        await env.Stores.Users.UpsertAsync(Support.Id, Support);
        var orgs = new OrganisationService(env.Stores, env.Clock, "Contoso", "11111111-2222-3333-4444-555555555555");
        var org = await orgs.CreateAsync(Support, new OrganisationBody("Northwind", "99999999-8888-7777-6666-555555555555"));
        var olivia = T.U("Olivia", admin: true) with { OrganisationId = org.Id };
        var oscar = T.U("Oscar") with { OrganisationId = org.Id };
        foreach (var u in new[] { olivia, oscar }) await env.Stores.Users.UpsertAsync(u.Id, u);
        return (org, olivia, oscar);
    }

    [Fact]
    public async Task Each_organisation_sees_only_its_own_people_and_recordings()
    {
        using var env = new Env();
        var (org, olivia, oscar) = await Northwind(env);
        var contoso = await env.ProcessedRecordingAsync(env.Alice);
        var northwind = await env.ProcessedRecordingAsync(oscar);
        var item = await env.Item(northwind);
        Assert.Equal(org.Id, item.OrganisationId);
        Assert.Null((await env.Item(contoso)).OrganisationId); // the first organisation's items stay as they were

        // Administrators reach only their own organisation's recordings.
        Assert.False(PermissionEvaluator.CanSee(env.Carol, item));
        await Assert.ThrowsAsync<ForbiddenException>(() => env.Meeting.GetItemAsync(env.Carol, northwind));
        await Assert.ThrowsAsync<ForbiddenException>(() => env.Meeting.GetItemAsync(olivia, contoso));
        Assert.Equal([northwind], (await env.Meeting.ListAsync(olivia, new ListQuery(null, null, null))).Select(r => r.Id));
        Assert.DoesNotContain(await env.Meeting.ListAsync(env.Carol, new ListQuery(null, null, null)), r => r.Id == northwind);
        Assert.NotNull(await env.Meeting.GetItemAsync(olivia, northwind));

        // People lists, and giving work, stop at the organisation's edge.
        Assert.Equal(new[] { olivia.Id, oscar.Id }.Order(), (await env.Meeting.ListUsersAsync(oscar)).Select(u => u.Id).Order());
        Assert.DoesNotContain(await env.Meeting.ListUsersAsync(env.Carol), u => u.Id == oscar.Id);
        await Assert.ThrowsAsync<DomainException>(() => env.Meeting.AssignAsync(oscar, northwind, env.Bob.Id, null));
        await Assert.ThrowsAsync<DomainException>(() => env.Meeting.AssignAsync(env.Alice, contoso, oscar.Id, null));

        // A reopen request is seen and agreed only by the organisation's own administrators.
        await env.Meeting.ApproveAsync(olivia, northwind);
        var request = await env.Meeting.RequestReopenAsync(oscar, northwind, "A name was wrong");
        Assert.Empty(await env.Meeting.ListReopenRequestsAsync(env.Carol));
        Assert.Single(await env.Meeting.ListReopenRequestsAsync(olivia));
        await Assert.ThrowsAsync<NotFoundException>(() => env.Meeting.ApproveReopenAsync(env.Carol, request));
    }

    [Fact]
    public async Task Each_organisation_has_its_own_settings_and_plan()
    {
        using var env = new Env();
        var (org, olivia, oscar) = await Northwind(env);

        // Vocabulary.
        await env.Vocabulary.AddAsync(env.Carol, "Contoso", null);
        await env.Vocabulary.AddAsync(olivia, "Northwind", null);
        Assert.Equal(["Contoso"], (await env.Vocabulary.ListAsync(env.Alice)).Select(t => t.Text));
        Assert.Equal(["Northwind"], (await env.Vocabulary.ListAsync(oscar)).Select(t => t.Text));

        // Departments.
        var departments = new DepartmentService(env.Stores, env.Clock);
        var legal = await departments.CreateAsync(env.Carol, "Legal", null);
        Assert.Empty((await departments.OverviewAsync(olivia)).Departments);
        await departments.CreateAsync(olivia, "Legal", null); // the same name is fine in another organisation
        await Assert.ThrowsAsync<NotFoundException>(() => departments.SetHandMemberAsync(olivia, legal.Id, oscar.Id, true));
        await Assert.ThrowsAsync<NotFoundException>(() => departments.SetHandMemberAsync(env.Carol, legal.Id, oscar.Id, true));

        // Letterhead spaces.
        var letterheads = new LetterheadService(env.Stores, env.Clock);
        await letterheads.SetPdfSpaceAsync(olivia, 70, 30, 30, 25);
        Assert.Equal(70, (await letterheads.GetAsync(olivia.Org)).PdfTopFirstMm);
        Assert.Equal(55, (await letterheads.GetAsync(env.Carol.Org)).PdfTopFirstMm);

        // Minutes templates.
        var templates = new MinutesTemplateService(env.Stores, env.Clock);
        var board = await templates.CreateAsync(olivia, new MinutesTemplateInput("Board", "{name}", "Arial", "#123456", "left", false,
            MinutesTemplate.StandardSections, true, null, false));
        Assert.Empty(await templates.ListAsync(env.Carol.Org));
        Assert.Equal("VerbaFlow standard", (await templates.PickAsync(env.Carol.Org, null)).Name);
        Assert.Equal("Board", (await templates.PickAsync(olivia.Org, null)).Name);
        await Assert.ThrowsAsync<DomainException>(() => templates.PickAsync(env.Carol.Org, board.Id));

        // Retention rules.
        var retention = new RetentionService(env.Stores, env.Meeting, env.Clock, env.Reporter);
        await retention.ProposeAsync(olivia, new RetentionChange(new RetentionRules(DeleteApprovedAfterDays: 365), null, null));
        Assert.Null((await retention.OverviewAsync(env.Carol)).Proposal);
        Assert.NotNull((await retention.OverviewAsync(olivia)).Proposal);
        await Assert.ThrowsAsync<DomainException>(() => retention.AgreeAsync(env.Dave)); // nothing waiting in Contoso

        // Plans and minutes are counted per organisation.
        Assert.NotEqual(BillingAccounts.Of(env.Alice), BillingAccounts.Of(oscar));
        Assert.Equal(org.Id, BillingAccounts.Of(oscar));
    }

    [Fact]
    public async Task Only_VerbaFlow_support_adds_organisations_and_a_directory_belongs_to_one()
    {
        using var env = new Env();
        var (org, olivia, _) = await Northwind(env);
        var orgs = new OrganisationService(env.Stores, env.Clock, "Contoso", "11111111-2222-3333-4444-555555555555");
        await Assert.ThrowsAsync<ForbiddenException>(() => orgs.CreateAsync(env.Carol, new OrganisationBody("Fabrikam", null)));
        await Assert.ThrowsAsync<ForbiddenException>(() => orgs.ListAsync(olivia));
        await Assert.ThrowsAsync<DomainException>(() => orgs.CreateAsync(Support, new OrganisationBody("Fabrikam", "99999999-8888-7777-6666-555555555555")));
        await Assert.ThrowsAsync<DomainException>(() => orgs.CreateAsync(Support, new OrganisationBody("Fabrikam", "11111111-2222-3333-4444-555555555555")));
        await Assert.ThrowsAsync<DomainException>(() => orgs.CreateAsync(Support, new OrganisationBody("Fabrikam", "not-a-directory")));
        await Assert.ThrowsAsync<DomainException>(() => orgs.CreateAsync(Support, new OrganisationBody("Northwind", null)));

        Assert.Equal((true, (Guid?)null), await orgs.ByTenantAsync("11111111-2222-3333-4444-555555555555"));
        Assert.Equal((true, (Guid?)org.Id), await orgs.ByTenantAsync("99999999-8888-7777-6666-555555555555"));
        Assert.False((await orgs.ByTenantAsync("00000000-1111-2222-3333-444444444444")).Found);

        var rows = await orgs.ListAsync(Support);
        Assert.Equal(["Contoso", "Northwind"], rows.Select(r => r.Name));
        Assert.Equal((2, 1), (rows[1].People, rows[1].Admins));

        var renamed = await orgs.ChangeAsync(Support, org.Id, new OrganisationBody("Northwind Traders", null));
        Assert.Null(renamed.TenantId);
        Assert.Equal("Northwind Traders", await orgs.NameAsync(org.Id));
        Assert.False((await orgs.ByTenantAsync("99999999-8888-7777-6666-555555555555")).Found);

        // Work accounts from that directory become the organisation's people, with its admin role.
        var users = new EntraUsers(env.Stores, env.Clock);
        var who = await users.ResolveAsync(new EntraIdentity("oid-1", "Nadia", "nadia@northwind.example", true, "", org.Id));
        Assert.Equal(org.Id, who.OrganisationId);
        Assert.True(who.IsAdmin && PermissionEvaluator.AdminOf(who, (await env.Item(await env.ProcessedRecordingAsync(olivia)))));
        // The same object id in another directory is someone else.
        Assert.NotEqual(who.Id, (await users.ResolveAsync(new EntraIdentity("oid-1", "Nadia", "nadia@contoso.example", false))).Id);
    }
}
