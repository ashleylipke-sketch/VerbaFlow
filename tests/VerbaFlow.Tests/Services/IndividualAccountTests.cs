using System.Security.Claims;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using VerbaFlow.Api;
using VerbaFlow.Core.Domain;
using VerbaFlow.Core.Security;
using VerbaFlow.Infrastructure.Services;

namespace VerbaFlow.Tests.Services;

/// <summary>People using VerbaFlow on their own (a personal Microsoft or Google account): their recordings and lists are theirs alone.</summary>
public class IndividualAccountTests
{
    private const string Tenant = "11111111-2222-3333-4444-555555555555";

    private static async Task<User> Personal(Env env, string name = "Sam Student")
    {
        var users = new EntraUsers(env.Stores, env.Clock);
        return await users.ResolvePersonalAsync(new PersonalIdentity("google:" + name, name, name.Replace(' ', '.') + "@gmail.test", "Google"));
    }

    [Fact]
    public async Task A_personal_account_is_never_seen_by_the_organisation_and_sees_nothing_of_it()
    {
        using var env = new Env();
        var sam = await Personal(env);
        Assert.True(sam.Individual);
        Assert.False(sam.IsAdmin);
        var orgItem = await env.ProcessedRecordingAsync(env.Alice);
        var samItem = await env.ProcessedRecordingAsync(sam);
        var item = await env.Item(samItem);
        Assert.True(item.Individual);

        // The organisation's administrator cannot open, list, approve or assign a personal recording.
        Assert.False(PermissionEvaluator.CanSee(env.Carol, item));
        await Assert.ThrowsAsync<ForbiddenException>(() => env.Meeting.GetItemAsync(env.Carol, samItem));
        Assert.DoesNotContain(await env.Meeting.ListAsync(env.Carol, new ListQuery(null, null, null)), r => r.Id == samItem);
        Assert.DoesNotContain(await env.Meeting.ListUsersAsync(env.Carol), u => u.Id == sam.Id);

        // The personal account sees only itself and its own recordings.
        Assert.Equal([samItem], (await env.Meeting.ListAsync(sam, new ListQuery(null, null, null))).Select(r => r.Id));
        Assert.Equal([sam.Id], (await env.Meeting.ListUsersAsync(sam)).Select(u => u.Id));
        await Assert.ThrowsAsync<ForbiddenException>(() => env.Meeting.GetItemAsync(sam, orgItem));

        // No giving work across the line, either way.
        await Assert.ThrowsAnyAsync<Exception>(() => env.Meeting.AssignAsync(sam, samItem, env.Alice.Id, null));
        await Assert.ThrowsAnyAsync<Exception>(() => env.Meeting.AssignAsync(env.Alice, orgItem, sam.Id, null));
        var row = Assert.Single(await env.Meeting.ListAsync(sam, new ListQuery(null, null, null)));
        Assert.DoesNotContain("assign", row.Actions);
        Assert.DoesNotContain("reassign", row.Actions);

        // Departments never include a personal account.
        var departments = new DepartmentService(env.Stores, env.Clock);
        var legal = await departments.CreateAsync(env.Carol, "Legal", null);
        await Assert.ThrowsAsync<DomainException>(() => departments.SetHandMemberAsync(env.Carol, legal.Id, sam.Id, true));
        Assert.Empty((await departments.OverviewAsync(sam)).Departments);
    }

    [Fact]
    public async Task The_owner_of_a_personal_recording_reopens_and_deletes_it_alone()
    {
        using var env = new Env();
        var sam = await Personal(env);
        var id = await env.ProcessedRecordingAsync(sam);
        await env.Meeting.ApproveAsync(sam, id);
        Assert.Equal(ItemStatus.Completed, (await env.Item(id)).Status);

        var next = await env.Meeting.RequestReopenAsync(sam, id, "A name was wrong");
        var reopened = await env.Item(next);
        Assert.Equal(2, reopened.VersionNo);
        Assert.True(reopened.Individual);
        await Assert.ThrowsAsync<DomainException>(() => env.Meeting.RequestDeleteAsync(sam, next, "Not needed"));

        await env.Meeting.DeleteAsync(sam, next, "Not needed");
        Assert.Equal(ItemStatus.Purged, (await env.Item(next)).Status);
        Assert.Equal(ItemStatus.Purged, (await env.Item(id)).Status);
    }

    [Fact]
    public async Task Vocabulary_and_saved_text_are_kept_apart()
    {
        using var env = new Env();
        var sam = await Personal(env);
        var ann = await Personal(env, "Ann Other");
        await env.Vocabulary.AddAsync(env.Carol, "Fernandes", null);
        await env.Vocabulary.AddAsync(sam, "Thermodynamics", null);
        Assert.Equal(["Fernandes"], (await env.Vocabulary.ListAsync(env.Alice)).Select(t => t.Text));
        Assert.Equal(["Thermodynamics"], (await env.Vocabulary.ListAsync(sam)).Select(t => t.Text));
        Assert.Empty(await env.Vocabulary.ListAsync(ann));
        var samTerm = (await env.Vocabulary.ListAsync(sam))[0];
        await Assert.ThrowsAnyAsync<Exception>(() => env.Vocabulary.RemoveAsync(env.Carol, samTerm.Id));

        await env.SavedText.AddAsync(env.Carol, "legal 1.1", "Firm wording.");
        var mine = await env.SavedText.AddAsync(sam, "sign off", "Best wishes, Sam");
        Assert.Equal(["sign off"], (await env.SavedText.ListAsync(sam)).Select(t => t.Code));
        Assert.Equal(["legal 1.1"], (await env.SavedText.ListAsync(env.Bob)).Select(t => t.Code));
        await Assert.ThrowsAnyAsync<Exception>(() => env.SavedText.RemoveAsync(ann, mine.Id));
        await Assert.ThrowsAnyAsync<Exception>(() => env.Vocabulary.AddAsync(env.Alice, "Nope", null)); // still admins only in the organisation
    }

    [Fact]
    public async Task A_personal_sign_in_is_matched_by_its_account_and_never_made_admin()
    {
        using var env = new Env();
        var users = new EntraUsers(env.Stores, env.Clock);
        var first = await users.ResolvePersonalAsync(new PersonalIdentity("microsoft:9188040d:abc", "Sam", "sam@outlook.test", "personal Microsoft account"));
        var again = await users.ResolvePersonalAsync(new PersonalIdentity("microsoft:9188040d:abc", "Sam S", "sam@outlook.test", "personal Microsoft account"));
        Assert.Equal(first.Id, again.Id);
        Assert.Equal("Sam S", (await env.Stores.Users.GetAsync(first.Id))!.Name);
        Assert.False(again.IsAdmin || again.IsSupport);
        var other = await users.ResolvePersonalAsync(new PersonalIdentity("google:abc", "Sam", "sam@gmail.test", "Google"));
        Assert.NotEqual(first.Id, other.Id);
    }

    private static ClaimsPrincipal Token(params (string Type, string Value)[] claims) =>
        new(new ClaimsIdentity(claims.Select(c => new Claim(c.Type, c.Value)), "Bearer"));

    [Fact]
    public void Microsoft_accounts_from_outside_the_organisation_are_personal_only_when_allowed()
    {
        var on = new SignInOptions { TenantId = Tenant, ClientId = "c", PersonalAccounts = true };
        var off = new SignInOptions { TenantId = Tenant, ClientId = "c" };
        var consumer = Token(("tid", SignInOptions.ConsumersTenant), ("scp", "access_as_user"), ("oid", "o1"), ("name", "Sam"));
        Assert.Null(SignIn.Identity(consumer, on)); // not one of the organisation's people
        var who = SignIn.PersonalIdentityOf(consumer, on);
        Assert.Equal($"microsoft:{SignInOptions.ConsumersTenant}:o1", who!.SignInId);
        Assert.Null(SignIn.PersonalIdentityOf(consumer, off));
        Assert.Null(SignIn.PersonalIdentityOf(Token(("tid", SignInOptions.ConsumersTenant), ("oid", "o1")), on)); // not a token for VerbaFlow
        Assert.Null(SignIn.PersonalIdentityOf(Token(("tid", Tenant), ("scp", "access_as_user"), ("oid", "o1")), on)); // the organisation's own
    }

    [Fact]
    public void VerbaFlow_sign_ins_last_thirty_days_and_cannot_be_changed()
    {
        var clock = new FixedClock();
        var dir = Directory.CreateTempSubdirectory("verbaflow-keys-").FullName;
        try
        {
            var tokens = new SessionTokens(DataProtectionProvider.Create(new DirectoryInfo(dir)), clock);
            var id = Guid.NewGuid();
            var t = tokens.Issue(id);
            Assert.StartsWith(SessionTokens.Prefix, t);
            Assert.Equal(id, tokens.Read(t));
            Assert.Null(tokens.Read(t[..^3] + "abc"));
            Assert.Null(tokens.Read("vf1.nonsense"));
            Assert.Null(tokens.Read("eyJ.something"));
            clock.Now += TimeSpan.FromDays(31);
            Assert.Null(tokens.Read(t));
        }
        finally { try { Directory.Delete(dir, true); } catch { /* best effort */ } }
    }

    private sealed class FixedConfig(OpenIdConnectConfiguration c) : IConfigurationManager<OpenIdConnectConfiguration>
    {
        public Task<OpenIdConnectConfiguration> GetConfigurationAsync(CancellationToken cancel) => Task.FromResult(c);
        public void RequestRefresh() { }
    }

    [Fact]
    public async Task A_google_sign_in_must_be_signed_by_google_and_made_for_VerbaFlow()
    {
        var key = new SymmetricSecurityKey(new byte[32].Select((_, i) => (byte)(i * 3 + 5)).ToArray());
        var config = new OpenIdConnectConfiguration();
        config.SigningKeys.Add(key);
        var google = new GoogleSignIn("verbaflow.apps.googleusercontent.com", new FixedConfig(config));
        string Make(string issuer = "https://accounts.google.com", string audience = "verbaflow.apps.googleusercontent.com", SecurityKey? k = null) =>
            new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
            {
                Issuer = issuer, Audience = audience, Expires = DateTime.UtcNow.AddMinutes(5),
                SigningCredentials = new SigningCredentials(k ?? key, SecurityAlgorithms.HmacSha256),
                Claims = new Dictionary<string, object> { ["sub"] = "1234", ["name"] = "Sam Student", ["email"] = "sam@gmail.test" },
            });
        var who = await google.CheckAsync(Make());
        Assert.Equal(new PersonalIdentity("google:1234", "Sam Student", "sam@gmail.test", "Google"), who);
        Assert.Null(await google.CheckAsync(Make(audience: "someone-else")));
        Assert.Null(await google.CheckAsync(Make(issuer: "https://evil.example")));
        Assert.Null(await google.CheckAsync(Make(k: new SymmetricSecurityKey(new byte[32]))));
        Assert.Null(await google.CheckAsync(""));
    }
}
