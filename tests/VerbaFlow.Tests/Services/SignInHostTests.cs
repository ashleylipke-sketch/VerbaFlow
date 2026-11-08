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
            b.UseSetting("Entra:PersonalAccounts", "true");
            b.ConfigureServices(s => s.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, o =>
            {
                o.Authority = null;
                o.Configuration = new OpenIdConnectConfiguration();
                o.TokenValidationParameters.IssuerSigningKey = _key;
            }));
        });
    }

    public void Dispose() { _app.Dispose(); try { Directory.Delete(_dir, true); } catch { /* best effort */ } }

    private string Token(string tenant = Tenant, string? roles = null, string audience = Client, string oid = "oid-ashley") => new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
    {
        Issuer = $"https://login.microsoftonline.com/{tenant}/v2.0", Audience = audience, Expires = DateTime.UtcNow.AddMinutes(10),
        SigningCredentials = new SigningCredentials(_key, SecurityAlgorithms.HmacSha256),
        Claims = new Dictionary<string, object>
        {
            ["tid"] = tenant, ["oid"] = oid, ["scp"] = "access_as_user", ["name"] = "Ashley Lipke", ["preferred_username"] = "ashley@firm.example",
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
        Assert.Equal(HttpStatusCode.Unauthorized, (await Me(r => r.Headers.Authorization = new("Bearer", Token(audience: "someone-else")))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Me(r => r.Headers.Authorization = new("Bearer", "not-a-token"))).StatusCode);

        var ok = await Me(r => r.Headers.Authorization = new("Bearer", Token(roles: "Admin")));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var me = await ok.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(("Ashley Lipke", true), (me.GetProperty("name").GetString(), me.GetProperty("isAdmin").GetBoolean()));
        var users = await (await Me(r => { r.RequestUri = new Uri("/api/users", UriKind.Relative); r.Headers.Authorization = new("Bearer", Token()); })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, users.GetArrayLength()); // the test users are not created online
    }

    [Fact]
    public async Task A_microsoft_account_from_outside_the_organisation_gets_a_personal_VerbaFlow()
    {
        var info = await _app.CreateClient().GetFromJsonAsync<JsonElement>("/api/sign-in");
        Assert.True(info.GetProperty("personalAccounts").GetBoolean());
        Assert.Equal("https://login.microsoftonline.com/common", info.GetProperty("microsoft").GetProperty("authority").GetString());

        var consumer = "9188040d-6c67-4c5b-b5f8-a3d2c3c9b6e1";
        var ok = await Me(r => r.Headers.Authorization = new("Bearer", Token(tenant: consumer, oid: "oid-sam")));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var me = await ok.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(me.GetProperty("individual").GetBoolean());
        Assert.False(me.GetProperty("isAdmin").GetBoolean());

        // A Microsoft sign-in is renewed with Microsoft, not by VerbaFlow; a made-up VerbaFlow sign-in is refused.
        var renew = new HttpRequestMessage(HttpMethod.Post, "/api/sign-in/renew");
        renew.Headers.Authorization = new("Bearer", Token(tenant: consumer, oid: "oid-sam"));
        Assert.Equal(HttpStatusCode.NoContent, (await _app.CreateClient().SendAsync(renew)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Me(r => r.Headers.Authorization = new("Bearer", "vf1.made-up"))).StatusCode);

        // A VerbaFlow sign-in made for this person (as after Google) gets in and renews itself.
        var id = Guid.Parse(me.GetProperty("id").GetString()!);
        var vf = _app.Services.GetRequiredService<VerbaFlow.Api.SessionTokens>().Issue(id);
        Assert.Equal(HttpStatusCode.OK, (await Me(r => r.Headers.Authorization = new("Bearer", vf))).StatusCode);
        var again = new HttpRequestMessage(HttpMethod.Post, "/api/sign-in/renew");
        again.Headers.Authorization = new("Bearer", vf);
        var fresh = await (await _app.CreateClient().SendAsync(again)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.StartsWith("vf1.", fresh.GetProperty("token").GetString());

        // Google is not set up here, so its exchange does not exist.
        Assert.Equal(HttpStatusCode.NotFound, (await _app.CreateClient().PostAsJsonAsync("/api/sign-in/google", new { credential = "x" })).StatusCode);
    }

    [Fact]
    public async Task A_widget_is_linked_to_a_personal_account_through_the_website_once()
    {
        var http = _app.CreateClient();
        var start = await (await http.PostAsJsonAsync("/api/widget-link/start", new { device = "Android keyboard" })).Content.ReadFromJsonAsync<JsonElement>();
        var code = start.GetProperty("code").GetString()!;
        var secret = start.GetProperty("secret").GetString()!;
        Assert.Matches("^[A-Z2-9]{4}-[A-Z2-9]{4}$", code);
        Assert.Equal(600, start.GetProperty("expiresInSeconds").GetInt32());

        async Task<HttpResponseMessage> Signed(HttpMethod m, string path, string token)
        {
            var req = new HttpRequestMessage(m, path);
            req.Headers.Authorization = new("Bearer", token);
            return await http.SendAsync(req);
        }
        async Task<HttpResponseMessage> Poll() => await http.PostAsJsonAsync("/api/widget-link/poll", new { secret });

        Assert.Equal((HttpStatusCode)202, (await Poll()).StatusCode);

        // The website needs a sign-in; a work account of this organisation signs in to the Widget directly instead.
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync($"/api/widget-link/{code}")).StatusCode);
        var work = await Signed(HttpMethod.Post, $"/api/widget-link/{code}/approve", Token());
        Assert.Equal(HttpStatusCode.BadRequest, work.StatusCode);
        Assert.Equal((HttpStatusCode)202, (await Poll()).StatusCode);

        // A personal account sees the waiting Widget (typed in small letters without the dash works too) and agrees.
        var personal = Token(tenant: "9188040d-6c67-4c5b-b5f8-a3d2c3c9b6e1", oid: "oid-student");
        var seen = await (await Signed(HttpMethod.Get, $"/api/widget-link/{code.Replace("-", "").ToLowerInvariant()}", personal)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Android keyboard", seen.GetProperty("device").GetString());
        Assert.Equal(HttpStatusCode.OK, (await Signed(HttpMethod.Post, $"/api/widget-link/{code}/approve", personal)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Signed(HttpMethod.Get, $"/api/widget-link/{code}", personal)).StatusCode);

        // The Widget collects VerbaFlow's own sign-in exactly once; it signs in as that person.
        var got = await Poll();
        Assert.Equal(HttpStatusCode.OK, got.StatusCode);
        var vf = (await got.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
        Assert.StartsWith("vf1.", vf);
        Assert.Equal((HttpStatusCode)410, (await Poll()).StatusCode);
        var me = await (await Me(r => r.Headers.Authorization = new("Bearer", vf))).Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(me.GetProperty("individual").GetBoolean());

        // A wrong secret or a made-up code gets nothing.
        Assert.Equal((HttpStatusCode)410, (await http.PostAsJsonAsync("/api/widget-link/poll", new { secret = "00" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Signed(HttpMethod.Get, "/api/widget-link/AAAA-BBBB", personal)).StatusCode);
    }
}
