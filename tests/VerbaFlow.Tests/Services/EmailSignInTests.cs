using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using VerbaFlow.Api;

namespace VerbaFlow.Tests.Services;

public class EmailSignInTests
{
    [Theory]
    [InlineData(" Sam.Student@Example.org ", "sam.student@example.org")]
    [InlineData("not an address", null)]
    [InlineData("two@@example.org", null)]
    [InlineData("nodot@localhost", null)]
    [InlineData("", null)]
    public void Addresses_are_tidied_or_refused(string typed, string? expected) => Assert.Equal(expected, EmailCodes.Clean(typed));

    [Fact]
    public void A_code_works_once_within_ten_minutes_and_five_wrong_guesses_end_it()
    {
        var clock = new FixedClock();
        var codes = new EmailCodes(clock);
        var code = codes.Make("sam@example.org")!;
        Assert.Matches("^[0-9]{6}$", code);
        Assert.Null(codes.Make("sam@example.org")); // not again within a minute
        Assert.False(codes.Check("other@example.org", code));
        Assert.True(codes.Check("sam@example.org", code[..3] + " " + code[3..]));
        Assert.False(codes.Check("sam@example.org", code)); // used

        clock.Now += TimeSpan.FromMinutes(2);
        var second = codes.Make("sam@example.org")!;
        var wrong = second == "000000" ? "111111" : "000000";
        for (var i = 0; i < EmailCodes.MaxTries; i++) Assert.False(codes.Check("sam@example.org", wrong));
        Assert.False(codes.Check("sam@example.org", second)); // too many wrong guesses

        clock.Now += TimeSpan.FromMinutes(2);
        var third = codes.Make("sam@example.org")!;
        clock.Now += EmailCodes.Lifetime;
        Assert.False(codes.Check("sam@example.org", third)); // run out

        for (var i = 0; i < 2; i++) { clock.Now += TimeSpan.FromMinutes(2); Assert.NotNull(codes.Make("sam@example.org")); }
        clock.Now += TimeSpan.FromMinutes(2);
        Assert.Null(codes.Make("sam@example.org")); // at most five an hour
    }

    private static WebApplicationFactory<Program> Host(string dir, string environment) => new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
    {
        b.UseEnvironment(environment);
        b.UseSetting("VerbaFlow:DataDir", dir);
        foreach (var k in new[] { "Speech:Endpoint", "Speech:Key", "OpenAI:Endpoint", "OpenAI:Key", "Entra:TenantId", "Entra:ClientId", "Database:ConnectionString", "Email:ConnectionString" })
            b.UseSetting(k, "");
        b.UseSetting("Entra:PersonalAccounts", "true");
    });

    [Fact]
    public async Task Someone_signs_in_with_an_emailed_code_and_gets_a_personal_VerbaFlow()
    {
        var dir = Directory.CreateTempSubdirectory("verbaflow-email-").FullName;
        try
        {
            using var app = Host(dir, "Development");
            var http = app.CreateClient();
            var info = await http.GetFromJsonAsync<JsonElement>("/api/sign-in");
            Assert.True(info.GetProperty("email").GetBoolean());

            Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsJsonAsync("/api/sign-in/email/start", new { email = "nonsense" })).StatusCode);

            async Task<string> CodeFor(string email)
            {
                Assert.Equal(HttpStatusCode.OK, (await http.PostAsJsonAsync("/api/sign-in/email/start", new { email })).StatusCode);
                var mail = Directory.GetFiles(Path.Combine(dir, "outbox"), "*.eml").OrderBy(f => f).Select(File.ReadAllText).Last(t => t.Contains(email, StringComparison.OrdinalIgnoreCase));
                var subject = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(Regex.Match(mail, @"Subject: =\?utf-8\?B\?([^?]+)\?=").Groups[1].Value));
                return Regex.Match(subject, @"sign-in code: (\d{6})").Groups[1].Value;
            }
            var code = await CodeFor("Sam@Example.org");
            Assert.Equal(6, code.Length);
            Assert.Equal((HttpStatusCode)429, (await http.PostAsJsonAsync("/api/sign-in/email/start", new { email = "sam@example.org" })).StatusCode);

            Assert.Equal(HttpStatusCode.Unauthorized, (await http.PostAsJsonAsync("/api/sign-in/email/finish", new { email = "sam@example.org", code = code == "000000" ? "111111" : "000000" })).StatusCode);
            var ok = await http.PostAsJsonAsync("/api/sign-in/email/finish", new { email = "sam@example.org", code, name = "Sam Student" });
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
            var token = (await ok.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
            Assert.StartsWith("vf1.", token);
            Assert.Equal(HttpStatusCode.Unauthorized, (await http.PostAsJsonAsync("/api/sign-in/email/finish", new { email = "sam@example.org", code })).StatusCode);

            var req = new HttpRequestMessage(HttpMethod.Get, "/api/me");
            req.Headers.Authorization = new("Bearer", token);
            var me = await (await http.SendAsync(req)).Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(me.GetProperty("individual").GetBoolean());
            Assert.Equal("Sam Student", me.GetProperty("name").GetString());
            Assert.Equal("sam@example.org", me.GetProperty("email").GetString());
        }
        finally { try { Directory.Delete(dir, true); } catch { /* best effort */ } }
    }

    [Fact]
    public async Task Online_email_sign_in_stays_off_until_real_email_is_set_up()
    {
        var dir = Directory.CreateTempSubdirectory("verbaflow-email-").FullName;
        try
        {
            using var app = Host(dir, "Production");
            var http = app.CreateClient();
            var info = await http.GetFromJsonAsync<JsonElement>("/api/sign-in");
            Assert.False(info.GetProperty("email").GetBoolean());
            Assert.Equal(HttpStatusCode.NotFound, (await http.PostAsJsonAsync("/api/sign-in/email/start", new { email = "sam@example.org" })).StatusCode);
        }
        finally { try { Directory.Delete(dir, true); } catch { /* best effort */ } }
    }

    [Fact]
    public async Task An_administrator_sends_a_test_email_and_others_cannot()
    {
        var dir = Directory.CreateTempSubdirectory("verbaflow-email-").FullName;
        try
        {
            using var app = Host(dir, "Development");
            var http = app.CreateClient();
            http.DefaultRequestHeaders.Add("X-Dev-User", "carol");
            var info = await http.GetFromJsonAsync<JsonElement>("/api/email-service");
            Assert.False(info.GetProperty("real").GetBoolean());
            Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsJsonAsync("/api/email-service/test", new { to = "nonsense" })).StatusCode);

            var sent = await (await http.PostAsJsonAsync("/api/email-service/test", new { to = (string?)null })).Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("Saved", sent.GetProperty("outcome").GetString());
            var to = sent.GetProperty("to").GetString()!;
            var mail = Directory.GetFiles(Path.Combine(dir, "outbox"), "*.eml").Select(File.ReadAllText).Single();
            Assert.Contains(to, mail, StringComparison.OrdinalIgnoreCase);

            var other = app.CreateClient();
            other.DefaultRequestHeaders.Add("X-Dev-User", "alice");
            Assert.Equal(HttpStatusCode.Forbidden, (await other.PostAsJsonAsync("/api/email-service/test", new { to = "x@example.org" })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await other.GetAsync("/api/email-service")).StatusCode);
        }
        finally { try { Directory.Delete(dir, true); } catch { /* best effort */ } }
    }
}
