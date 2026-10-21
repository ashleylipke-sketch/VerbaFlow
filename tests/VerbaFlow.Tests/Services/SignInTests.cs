using System.Security.Claims;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using VerbaFlow.Api;
using VerbaFlow.Core.Domain;
using VerbaFlow.Infrastructure.Services;

namespace VerbaFlow.Tests.Services;

public class SignInTests
{
    private const string Tenant = "11111111-2222-3333-4444-555555555555";
    private static readonly SignInOptions Options = new() { TenantId = Tenant, ClientId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee" };

    private static ClaimsPrincipal Token(params (string Type, string Value)[] claims) =>
        new(new ClaimsIdentity(claims.Select(c => new Claim(c.Type, c.Value)), "Bearer"));

    [Fact]
    public async Task A_first_microsoft_sign_in_creates_the_user_and_later_ones_follow_the_directory()
    {
        using var env = new Env();
        var users = new EntraUsers(env.Stores, env.Clock);
        var first = await users.ResolveAsync(new EntraIdentity("oid-1", "Ashley Lipke", "ashley@firm.example", false));
        Assert.Equal(("Ashley Lipke", "ashley@firm.example", false, false), (first.Name, first.Email, first.IsAdmin, first.IsSupport));
        Assert.Equal(first.Id, (await users.ResolveAsync(new EntraIdentity("oid-1", "Ashley Lipke", "ashley@firm.example", false))).Id);

        // Made an administrator in Microsoft 365: the next sign-in says so, at once, and History records it.
        var admin = await users.ResolveAsync(new EntraIdentity("oid-1", "Ashley Lipke", "ashley@firm.example", true));
        Assert.Equal(first.Id, admin.Id);
        Assert.True(admin.IsAdmin);
        Assert.True((await env.Stores.Users.GetAsync(first.Id))!.IsAdmin);
        var audit = (await env.Stores.Audit.ListAsync()).Where(a => a.Product == "platform").Select(a => a.Type).ToList();
        Assert.Equal(["user.first_sign_in", "user.admin_changed"], audit);

        var other = await users.ResolveAsync(new EntraIdentity("oid-2", null, "bob@firm.example", false));
        Assert.NotEqual(first.Id, other.Id);
        Assert.Equal("bob@firm.example", other.Name); // no display name: the email stands in
        await Assert.ThrowsAsync<ForbiddenException>(() => users.ResolveAsync(new EntraIdentity(" ", "x", "x", false)));
    }

    [Fact]
    public void Only_a_person_from_this_organisation_using_VerbaFlow_is_accepted()
    {
        var good = Token(("tid", Tenant), ("scp", "access_as_user"), ("oid", "oid-1"), ("name", "Ashley"), ("preferred_username", "a@firm.example"), ("roles", "Admin"));
        Assert.Equal(new EntraIdentity("oid-1", "Ashley", "a@firm.example", true), SignIn.Identity(good, Options));
        Assert.False(SignIn.Identity(Token(("tid", Tenant), ("scp", "access_as_user"), ("oid", "o"), ("roles", "Reader")), Options)!.IsAdmin);
        Assert.Null(SignIn.Identity(Token(("tid", "another-organisation"), ("scp", "access_as_user"), ("oid", "o")), Options));
        Assert.Null(SignIn.Identity(Token(("tid", Tenant), ("oid", "o"), ("roles", "Admin")), Options)); // an app's own token, not a person
        Assert.Null(SignIn.Identity(Token(("tid", Tenant), ("scp", "access_as_user")), Options));
    }

    private sealed class Host(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "VerbaFlow";
        public string ContentRootPath { get; set; } = ".";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    [Fact]
    public void Test_users_exist_only_in_development_and_can_be_switched_off_there()
    {
        IConfiguration Config(params (string, string)[] kv) => new ConfigurationBuilder().AddInMemoryCollection(kv.Select(x => new KeyValuePair<string, string?>(x.Item1, x.Item2))).Build();
        Assert.True(SignInOptions.From(Config(), new Host("Development")).TestUsers);
        Assert.False(SignInOptions.From(Config(), new Host("Production")).TestUsers);
        Assert.False(SignInOptions.From(Config(("SignIn:TestUsers", "false")), new Host("Development")).TestUsers);
        var o = SignInOptions.From(Config(("Entra:TenantId", Tenant), ("Entra:ClientId", "client")), new Host("Production"));
        Assert.True(o.Microsoft);
        Assert.Equal("api://client/access_as_user", o.Scope);
        Assert.False(SignInOptions.From(Config(("Entra:TenantId", Tenant)), new Host("Production")).Microsoft);
    }
}
