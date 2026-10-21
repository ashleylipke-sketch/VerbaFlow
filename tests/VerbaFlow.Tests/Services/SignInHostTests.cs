using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace VerbaFlow.Tests.Services;

/// <summary>The real server with Microsoft sign-in switched on, its tokens signed by a test key instead of Microsoft's.</summary>
public class SignInHostTests : IDisposable
{
    private const string Tenant = "11111111-2222-3333-4444-555555555555", Client = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
    private readonly string _dir = Directory.CreateTempSubdirectory("verbaflow-signin-").FullName;
    private readonly SymmetricSecurityKey _key = new(new byte[32].Select((_, i) => (byte)(i * 7 + 1)).ToArray());
    private readonly WebApplicationFactory<Program> _app;

    public SignInHostTests()
    {
        _app = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Production");
            b.UseSetting("VerbaFlow:DataDir", _dir);
            b.UseSetting("Entra:TenantId", Tenant);
            b.UseSetting("Entra:ClientId", Client);
            b.ConfigureServices(s => s.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, o =>
            {
                o.Authority = null;
                o.Configuration = new OpenIdConnectConfiguration();
                o.TokenValidationParameters.IssuerSigningKey = _key;
            }));
        });
    }

    public void Dispose() { _app.Dispose(); try { Directory.Delete(_dir, true); } catch { /* best effort */ } }

    private string Token(string tenant = Tenant, string? roles = null, string audience = Client) => new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
    {
        Issuer = $"https://login.microsoftonline.com/{tenant}/v2.0", Audience = audience, Expires = DateTime.UtcNow.AddMinutes(10),
        SigningCredentials = new SigningCredentials(_key, SecurityAlgorithms.HmacSha256),
        Claims = new Dictionary<string, object>
        {
            ["tid"] = tenant, ["oid"] = "oid-ashley", ["scp"] = "access_as_user", ["name"] = "Ashley Lipke", ["preferred_username"] = "ashley@firm.example",
            ["roles"] = roles is null ? Array.Empty<string>() : new[] { roles },
        },
    });

    private async Task<HttpResponseMessage> Me(Action<HttpRequestMessage> sign)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, "/api/me");
        sign(req);
        return await _app.CreateClient().SendAsync(req);
    }

    [Fact]
    public async Task Online_only_a_microsoft_sign_in_from_this_organisation_gets_in()
    {
        var info = await _app.CreateClient().GetFromJsonAsync<JsonElement>("/api/sign-in");
        Assert.False(info.GetProperty("testUsers").GetBoolean());
        Assert.Equal($"api://{Client}/access_as_user", info.GetProperty("microsoft").GetProperty("scope").GetString());

        Assert.Equal(HttpStatusCode.Unauthorized, (await Me(_ => { })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Me(r => r.Headers.Add("X-Dev-User", "carol"))).StatusCode); // no test users online
        Assert.Equal(HttpStatusCode.Unauthorized, (await Me(r => r.Headers.Authorization = new("Bearer", Token(tenant: "99999999-2222-3333-4444-555555555555")))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Me(r => r.Headers.Authorization = new("Bearer", Token(audience: "someone-else")))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Me(r => r.Headers.Authorization = new("Bearer", "not-a-token"))).StatusCode);

        var ok = await Me(r => r.Headers.Authorization = new("Bearer", Token(roles: "Admin")));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var me = await ok.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(("Ashley Lipke", true), (me.GetProperty("name").GetString(), me.GetProperty("isAdmin").GetBoolean()));
        var users = await (await Me(r => { r.RequestUri = new Uri("/api/users", UriKind.Relative); r.Headers.Authorization = new("Bearer", Token()); })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, users.GetArrayLength()); // the test users are not created online
    }
}
