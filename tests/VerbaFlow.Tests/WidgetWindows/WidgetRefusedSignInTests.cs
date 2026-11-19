using System.Net;
using System.Text;
using VerbaFlow.Widget;

namespace VerbaFlow.Tests.WidgetWindows;

/// <summary>A sign-in VerbaFlow refuses (403): the Windows Widget lets the sign-in change, then tries once more.</summary>
public sealed class WidgetRefusedSignInTests
{
    private sealed class Server(Func<HttpRequestMessage, (HttpStatusCode, string)> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var (code, body) = request.RequestUri!.AbsolutePath == "/api/sign-in"
                ? (HttpStatusCode.OK, """{"testUsers":false,"microsoft":null,"personalAccounts":true}""")
                : answer(request);
            return Task.FromResult(new HttpResponseMessage(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private sealed class TwoWays : ISignIn
    {
        public string Token = "old-link";
        public readonly List<string?> Refusals = [];
        public bool Switch;
        public Task<KeyValuePair<string, string>> HeaderAsync(SignInInfo info, CancellationToken ct) =>
            Task.FromResult(new KeyValuePair<string, string>("Authorization", "Bearer " + Token));
        public Task ForgetAsync() => Task.CompletedTask;
        public Task<bool> RefusedAsync(string? signInAt)
        {
            Refusals.Add(signInAt);
            if (signInAt is not null && Switch) throw new DirectorySwitchException("Sign in again.");
            if (Token != "old-link") return Task.FromResult(false);
            Token = "microsoft";
            return Task.FromResult(true);
        }
    }

    private static VerbaFlowClient Client(ISignIn s, Func<HttpRequestMessage, (HttpStatusCode, string)> answer) =>
        new(new HttpClient(new Server(answer)) { BaseAddress = new Uri("http://verbaflow.test/") }, s);

    [Fact]
    public async Task An_old_website_link_that_is_refused_is_dropped_and_the_next_sign_in_is_used()
    {
        var s = new TwoWays();
        var client = Client(s, r => r.Headers.Authorization!.Parameter == "microsoft"
            ? (HttpStatusCode.OK, """{"name":"Ash","isAdmin":true}""")
            : (HttpStatusCode.Forbidden, """{"error":"Already signs in another way."}"""));
        Assert.Equal(("Ash", true), await client.MeAsync());
        Assert.Equal([null], s.Refusals);
    }

    [Fact]
    public async Task A_refusal_that_cannot_be_helped_shows_the_plain_sentence_after_one_try()
    {
        var s = new TwoWays();
        var client = Client(s, _ => (HttpStatusCode.Forbidden, """{"error":"Already signs in another way."}"""));
        var e = await Assert.ThrowsAsync<WidgetException>(() => client.MeAsync());
        Assert.Equal("Already signs in another way.", e.Message);
        Assert.Single(s.Refusals);
    }

    [Fact]
    public async Task The_organisation_s_directory_is_passed_on_so_the_person_signs_in_again_there()
    {
        var s = new TwoWays { Token = "microsoft", Switch = true };
        var client = Client(s, _ => (HttpStatusCode.Forbidden, """{"error":"Use your work account.","signInAt":"tenant-1"}"""));
        await Assert.ThrowsAsync<DirectorySwitchException>(() => client.MeAsync());
        Assert.Equal(["tenant-1"], s.Refusals);
    }
}
