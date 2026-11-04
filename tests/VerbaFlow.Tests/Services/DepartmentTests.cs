using System.Security.Claims;
using VerbaFlow.Api;
using VerbaFlow.Core.Domain;
using VerbaFlow.Core.Security;
using VerbaFlow.Infrastructure.Services;

namespace VerbaFlow.Tests.Services;

public class DepartmentTests
{
    private const string LegalGroup = "6f1b2c3d-1111-2222-3333-444455556666";

    private static DepartmentService Service(Env env) => new(env.Stores, env.Clock);

    [Fact]
    public async Task People_in_the_same_department_can_open_each_others_recordings_but_not_change_them()
    {
        using var env = new Env();
        var departments = Service(env);
        var legal = await departments.CreateAsync(env.Carol, "Legal", null);
        await departments.SetHandMemberAsync(env.Carol, legal.Id, env.Alice.Id, true);
        await departments.SetHandMemberAsync(env.Carol, legal.Id, env.Bob.Id, true);
        var id = await env.ProcessedRecordingAsync(env.Alice);

        var bob = await departments.WithColleaguesAsync(env.Bob);
        var xavier = await departments.WithColleaguesAsync(env.Xavier);
        Assert.Contains(env.Alice.Id, bob.Colleagues);
        Assert.Empty(xavier.Colleagues);

        var item = await env.Item(id);
        Assert.Equal(ItemRole.Colleague, PermissionEvaluator.RoleOn(bob, item));
        Assert.True(PermissionEvaluator.Evaluate(bob, item, Capability.Read).Allowed);
        foreach (var c in new[] { Capability.Write, Capability.Reassign, Capability.Approve, Capability.Export, Capability.ExportAudio })
            Assert.False(PermissionEvaluator.Evaluate(bob, item, c).Allowed);
        Assert.False(PermissionEvaluator.CanSee(xavier, item));
        await Assert.ThrowsAsync<ForbiddenException>(() => env.Meeting.GetItemAsync(xavier, id));

        var detail = await env.Meeting.GetItemAsync(bob, id);
        Assert.False(detail.CanEditTranscript);
        Assert.False(detail.CanExport);
        var row = Assert.Single(await env.Meeting.ListAsync(bob, new ListQuery(null, null, null)));
        Assert.True(row.FromDepartment);
        Assert.Equal(["open"], row.Actions);
        Assert.False((await env.Meeting.ListAsync(env.Alice, new ListQuery(null, null, null)))[0].FromDepartment);
        Assert.Empty(await env.Meeting.ListAsync(xavier, new ListQuery(null, null, null)));

        await env.Meeting.ApproveAsync(env.Alice, id);
        Assert.True(PermissionEvaluator.Evaluate(bob, await env.Item(id), Capability.Export).Allowed);
    }

    [Fact]
    public async Task An_unfinished_dictation_stays_the_authors_own()
    {
        using var env = new Env();
        var departments = Service(env);
        var legal = await departments.CreateAsync(env.Carol, "Legal", null);
        await departments.SetHandMemberAsync(env.Carol, legal.Id, env.Alice.Id, true);
        await departments.SetHandMemberAsync(env.Carol, legal.Id, env.Bob.Id, true);
        var id = await env.Meeting.SaveDraftAsync(env.Alice, null,
            new DraftUpload(Env.Audio("first part"), "dictation.wav", "audio/wav", 10_000, "Letter", "en", "en-GB", null, "letter"));
        var bob = await departments.WithColleaguesAsync(env.Bob);
        Assert.False(PermissionEvaluator.CanSee(bob, await env.Item(id)));
    }

    [Fact]
    public async Task Members_come_from_the_linked_Microsoft_group()
    {
        using var env = new Env();
        var departments = Service(env);
        var users = new EntraUsers(env.Stores, env.Clock);
        var ann = await users.ResolveAsync(new EntraIdentity("oid-ann", "Ann", "ann@firm.example", false, LegalGroup));
        var ben = await users.ResolveAsync(new EntraIdentity("oid-ben", "Ben", "ben@firm.example", false, LegalGroup + ",0e0e0e0e-0000-0000-0000-000000000001"));
        var cat = await users.ResolveAsync(new EntraIdentity("oid-cat", "Cat", "cat@firm.example", false));

        var overview = await departments.OverviewAsync(env.Carol);
        Assert.Contains(overview.GroupsNotLinked, g => g.GroupId == LegalGroup && g.People == 2 && g.Names.SequenceEqual(["Ann", "Ben"]));
        Assert.Empty((await departments.OverviewAsync(ann)).GroupsNotLinked); // only administrators see Microsoft groups

        await departments.CreateAsync(env.Carol, "Legal", LegalGroup.ToUpperInvariant());
        Assert.Equal([ben.Id], (await departments.WithColleaguesAsync(ann)).Colleagues);
        Assert.Empty((await departments.WithColleaguesAsync(cat)).Colleagues);
        Assert.Equal(["Legal"], (await departments.OverviewAsync(ann)).Mine);
        Assert.DoesNotContain((await departments.OverviewAsync(env.Carol)).GroupsNotLinked, g => g.GroupId == LegalGroup);

        // Ben leaves the group in Microsoft: at his next sign-in he leaves the department.
        env.Clock.Now += EntraUsers.Remember + TimeSpan.FromMinutes(1);
        await users.ResolveAsync(new EntraIdentity("oid-ben", "Ben", "ben@firm.example", false));
        Assert.Empty((await departments.WithColleaguesAsync(ann)).Colleagues);
        Assert.Contains(await env.Stores.Audit.ListAsync(), e => e.Type == "user.groups_changed");
    }

    [Fact]
    public async Task Only_administrators_change_departments_and_Microsoft_users_are_not_added_by_hand()
    {
        using var env = new Env();
        var departments = Service(env);
        await Assert.ThrowsAsync<ForbiddenException>(() => departments.CreateAsync(env.Alice, "Legal", null));
        var legal = await departments.CreateAsync(env.Carol, " Legal ", LegalGroup);
        Assert.Equal("Legal", legal.Name);
        await Assert.ThrowsAsync<DomainException>(() => departments.CreateAsync(env.Dave, "legal", null));
        await Assert.ThrowsAsync<DomainException>(() => departments.CreateAsync(env.Dave, "Finance", LegalGroup));
        await Assert.ThrowsAsync<DomainException>(() => departments.CreateAsync(env.Dave, "Finance", "not a group id"));
        await Assert.ThrowsAsync<DomainException>(() => departments.CreateAsync(env.Dave, "  ", null));

        var ann = await new EntraUsers(env.Stores, env.Clock).ResolveAsync(new EntraIdentity("oid-ann", "Ann", "ann@firm.example", false));
        await Assert.ThrowsAsync<DomainException>(() => departments.SetHandMemberAsync(env.Carol, legal.Id, ann.Id, true));
        await Assert.ThrowsAsync<ForbiddenException>(() => departments.SetHandMemberAsync(env.Alice, legal.Id, env.Alice.Id, true));

        var renamed = await departments.ChangeAsync(env.Dave, legal.Id, "Legal and Compliance", null);
        Assert.Null(renamed.GroupId);
        await departments.RemoveAsync(env.Dave, legal.Id);
        Assert.Empty((await departments.OverviewAsync(env.Carol)).Departments);
        var types = (await env.Stores.Audit.ListAsync()).Select(e => e.Type).ToList();
        Assert.Contains("department.created", types);
        Assert.Contains("department.changed", types);
        Assert.Contains("department.removed", types);
    }

    [Fact]
    public void Groups_are_read_from_the_sign_in()
    {
        static ClaimsPrincipal Token(params (string Type, string Value)[] claims) =>
            new(new ClaimsIdentity(claims.Select(c => new Claim(c.Type, c.Value)), "Bearer"));
        Assert.Equal("0e0e0e0e-0000-0000-0000-000000000001," + LegalGroup,
            SignIn.Groups(Token(("groups", LegalGroup), ("groups", "0E0E0E0E-0000-0000-0000-000000000001"), ("groups", "not-a-guid"))));
        Assert.Equal("", SignIn.Groups(Token(("oid", "x"))));
        Assert.Equal(EntraUsers.TooManyGroups, SignIn.Groups(Token(("_claim_names", "{\"groups\":\"src1\"}"))));
        Assert.Equal(EntraUsers.TooManyGroups, SignIn.Groups(Token(("hasgroups", "true"))));
    }
}
