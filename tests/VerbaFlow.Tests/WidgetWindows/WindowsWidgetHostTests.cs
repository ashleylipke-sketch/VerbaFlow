using System.Net.WebSockets;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using VerbaFlow.Widget;

namespace VerbaFlow.Tests.WidgetWindows;

/// <summary>
/// The Widget for Windows' own code (everything but the Windows windows, microphone and paste) against the real
/// VerbaFlow server, with stand-in live speech: one placeholder word for every 400 ms of sound.
/// </summary>
public class WindowsWidgetHostTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("verbaflow-winwidget-").FullName;
    private readonly WebApplicationFactory<Program> _app;

    public WindowsWidgetHostTests()
    {
        _app = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development");
            b.UseSetting("VerbaFlow:DataDir", _dir);
            // Stand-ins, even on a computer whose VerbaFlow has real keys set.
            foreach (var k in new[] { "Speech:Endpoint", "Speech:Key", "OpenAI:Endpoint", "OpenAI:Key", "Entra:TenantId", "Entra:ClientId", "Database:ConnectionString" })
                b.UseSetting(k, "");
        });
    }

    public void Dispose() { _app.Dispose(); try { Directory.Delete(_dir, true); } catch { /* best effort */ } }

    private sealed class TestUser(string user) : ISignIn
    {
        public Task<KeyValuePair<string, string>> HeaderAsync(SignInInfo info, CancellationToken ct) =>
            info.TestUsers ? Task.FromResult(new KeyValuePair<string, string>("X-Dev-User", user)) : throw new SignInNeededException("Sign in first.");
        public Task ForgetAsync() => Task.CompletedTask;
    }

    private VerbaFlowClient Client(string user = "alice") => new(_app.CreateClient(), new TestUser(user));

    /// <summary>Opens the live connection through the test server; the hook can break it on purpose.</summary>
    private Func<Uri, CancellationToken, Task<WebSocket>> Connect(List<WebSocket>? opened = null) => async (uri, ct) =>
    {
        var ws = await _app.Server.CreateWebSocketClient().ConnectAsync(uri, ct);
        opened?.Add(ws);
        return ws;
    };

    private static async Task Speak(LiveListener l, int ms)
    {
        for (var at = 0; at < ms; at += 100) { l.Write(new byte[100 * LiveListener.BytesPerMs]); await Task.Delay(5); }
    }

    private static string Text(LiveText? t) => t is null ? "" : DictationText.Heard(t.Segments, t.StartsWithBreak);

    private const string Expected = "stand-in live text, the speech service is not connected. Each word stands for a little of what you said.";

    [Fact]
    public async Task A_dictation_from_a_windows_program_comes_back_as_text_and_only_facts_are_kept()
    {
        var client = Client();
        var info = await client.SignInInfoAsync();
        Assert.True(info.TestUsers);
        Assert.Null(info.Microsoft);
        Assert.Equal(("Alice Author", false), await client.MeAsync());
        Assert.Contains(await client.LanguagesAsync(), l => l.Code == "en-GB" && l.Supported);

        var start = await client.StartAsync("outlook.exe", "Windows program", "en-GB");
        LiveText? last = null;
        await using var listener = new LiveListener(client, start, Connect());
        listener.Text += t => last = t;
        listener.Start();
        await Speak(listener, 9_600); // the stand-in script up to "new paragraph", 24 words
        listener.Stop();
        var r = await listener.Stopped.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal((true, 9_600, 0), (r.Complete, r.SpokenMs, r.Reconnects));
        Assert.Equal(Expected, Text(last));

        var fix = await client.FixGrammarAsync(start.Id, "i think so");
        Assert.Equal("I think so", fix.Text); // the stand-in grammar check: a capital at the start, capital I
        await client.EndAsync(start.Id, true, r.SpokenMs, DictationText.Words(Expected), 0);

        using var http = _app.CreateClient();
        http.DefaultRequestHeaders.Add("X-Dev-User", "alice");
        var uses = await http.GetStringAsync("/api/widget/sessions");
        Assert.Contains("\"site\":\"outlook.exe\"", uses);
        Assert.Contains("\"device\":\"Windows program\"", uses);
        Assert.Contains("\"outcome\":\"accepted\"", uses);
        Assert.DoesNotContain("each word stands", uses, StringComparison.OrdinalIgnoreCase); // no words are kept
    }

    [Fact]
    public async Task A_lost_connection_is_made_again_and_no_word_is_missed_or_doubled()
    {
        var client = Client();
        var start = await client.StartAsync("winword.exe", "Windows program", "en-GB");
        var opened = new List<WebSocket>();
        LiveText? last = null;
        var links = new List<bool>();
        await using var listener = new LiveListener(client, start, Connect(opened));
        listener.Text += t => last = t;
        listener.Link += live => { lock (links) links.Add(live); };
        listener.Start();
        await Speak(listener, 4_000);
        await WaitFor(() => DictationText.Words(Text(last)) >= 9); // the first phrase of eight words is finished
        opened[0].Abort(); // the network drops
        await Speak(listener, 5_600);
        listener.Stop();
        var r = await listener.Stopped.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(r.Complete);
        Assert.Equal(1, r.Reconnects);
        Assert.True(opened.Count >= 2);
        lock (links) Assert.Contains(false, links);
        Assert.Equal(Expected, Text(last));
    }

    [Fact]
    public async Task An_administrator_can_switch_the_widget_off_in_a_program()
    {
        using var http = _app.CreateClient();
        http.DefaultRequestHeaders.Add("X-Dev-User", "carol");
        var put = await http.PutAsync("/api/widget/policy", new StringContent("{\"blockedSites\":[\"Teams.exe\",\"mail.example.com\"]}", System.Text.Encoding.UTF8, "application/json"));
        Assert.True(put.IsSuccessStatusCode);

        var client = Client("bob");
        Assert.Equal(["mail.example.com", "teams.exe"], await client.BlockedAsync());
        var e = await Assert.ThrowsAsync<WidgetException>(() => client.StartAsync("teams.exe", "Windows program", "en-GB"));
        Assert.Equal("Your administrator has switched the Widget off here.", e.Message);
        var s = await client.StartAsync("outlook.exe", "Windows program", "en-GB");
        await client.EndAsync(s.Id, false, 0, null, 0);
    }

    [Fact]
    public async Task VerbaFlow_not_running_gives_a_plain_sentence()
    {
        var client = new VerbaFlowClient(new HttpClient { BaseAddress = new Uri("http://127.0.0.1:9/") }, new TestUser("alice"));
        var e = await Assert.ThrowsAsync<WidgetException>(() => client.StartAsync("outlook.exe", "Windows program", "en-GB"));
        Assert.Equal(VerbaFlowClient.Unreachable, e.Message);
    }

    private static async Task WaitFor(Func<bool> ok)
    {
        for (var i = 0; i < 200 && !ok(); i++) await Task.Delay(50);
        Assert.True(ok());
    }
}
