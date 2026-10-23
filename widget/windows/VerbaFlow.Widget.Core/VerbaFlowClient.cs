using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace VerbaFlow.Widget;

/// <summary>A plain sentence for the person, written by VerbaFlow or by the Widget. Never technical detail.</summary>
public class WidgetException(string message) : Exception(message);
/// <summary>The person needs to sign in (again) before VerbaFlow can be used.</summary>
public sealed class SignInNeededException(string message) : WidgetException(message);

public sealed record MicrosoftSignIn(string TenantId, string ClientId, string Scope, string Authority);
public sealed record SignInInfo(bool TestUsers, MicrosoftSignIn? Microsoft);
public sealed record WidgetStart(Guid Id, Guid LiveId, string Ticket, string Engine);
public sealed record SpokenLanguage(string Code, string Name, bool Supported);
public sealed record GrammarFix(string Text, int LinesChanged, int LeftOutAsRewrites);

/// <summary>
/// Gives the sign-in to send with a call: an Authorization header with a Microsoft token, or X-Dev-User for a test user.
/// Throws <see cref="SignInNeededException"/> when the person must sign in first.
/// </summary>
public interface ISignIn
{
    Task<KeyValuePair<string, string>> HeaderAsync(SignInInfo info, CancellationToken ct);
    /// <summary>VerbaFlow refused the sign-in: forget it, so the next call asks again.</summary>
    Task ForgetAsync();
}

/// <summary>The calls the Widget makes to VerbaFlow: the same as the browser Widget's.</summary>
public sealed class VerbaFlowClient(HttpClient http, ISignIn signIn)
{
    public const string Unreachable = "VerbaFlow cannot be reached. Check that it is running, and the address in the Widget's settings.";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private SignInInfo? _info;
    private DateTimeOffset _infoAt;

    public Uri Server => http.BaseAddress ?? throw new InvalidOperationException("No server address.");

    /// <summary>How this VerbaFlow signs people in (public details), remembered for ten minutes.</summary>
    public async Task<SignInInfo> SignInInfoAsync(CancellationToken ct = default)
    {
        if (_info is not null && DateTimeOffset.UtcNow - _infoAt < TimeSpan.FromMinutes(10)) return _info;
        try
        {
            _info = await http.GetFromJsonAsync<SignInInfo>("api/sign-in", Json, ct) ?? throw new WidgetException(Unreachable);
            _infoAt = DateTimeOffset.UtcNow;
            return _info;
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or NotSupportedException or TaskCanceledException && !ct.IsCancellationRequested)
        { throw new WidgetException(Unreachable); }
    }

    /// <summary>Who VerbaFlow says is signed in: their name, and whether they are an administrator.</summary>
    public async Task<(string Name, bool IsAdmin)> MeAsync(CancellationToken ct = default)
    {
        var me = await SendAsync<JsonElement>(HttpMethod.Get, "api/me", null, ct);
        return (me.GetProperty("name").GetString() ?? "", me.TryGetProperty("isAdmin", out var a) && a.GetBoolean());
    }

    /// <param name="program">The program's file name, for example outlook.exe: what the history records and admins can block.</param>
    public Task<WidgetStart> StartAsync(string program, string device, string languages, CancellationToken ct = default) =>
        SendAsync<WidgetStart>(HttpMethod.Post, "api/widget/start", new { site = program, device, spokenLanguages = languages }, ct);

    public Task<WidgetStart> ListenAgainAsync(Guid id, CancellationToken ct = default) =>
        SendAsync<WidgetStart>(HttpMethod.Post, $"api/widget/{id}/listen-again", null, ct);

    public Task EndAsync(Guid id, bool accepted, int? spokenMs, int? words, int? reconnects, CancellationToken ct = default) =>
        SendAsync<JsonElement?>(HttpMethod.Post, $"api/widget/{id}/end",
            new { outcome = accepted ? "accepted" : "cancelled", spokenMs, words, reconnects }, ct);

    public Task<GrammarFix> FixGrammarAsync(Guid id, string text, CancellationToken ct = default) =>
        SendAsync<GrammarFix>(HttpMethod.Post, $"api/widget/{id}/grammar", new { text }, ct);

    public async Task<IReadOnlyList<string>> BlockedAsync(CancellationToken ct = default) =>
        (await SendAsync<JsonElement>(HttpMethod.Get, "api/widget/policy", null, ct)).GetProperty("blockedSites")
            .EnumerateArray().Select(e => e.GetString() ?? "").Where(s => s.Length > 0).ToList();

    /// <summary>The languages VerbaFlow can transcribe, for the settings.</summary>
    public async Task<IReadOnlyList<SpokenLanguage>> LanguagesAsync(CancellationToken ct = default) =>
        (await SendAsync<JsonElement>(HttpMethod.Get, "api/languages", null, ct)).GetProperty("languages").Deserialize<List<SpokenLanguage>>(Json) ?? [];

    /// <summary>A new single-use ticket for the live connection (after a lost connection).</summary>
    public async Task<string> TicketAsync(Guid liveId, CancellationToken ct = default) =>
        (await SendAsync<JsonElement>(HttpMethod.Post, $"api/live/{liveId}/ticket", null, ct)).GetProperty("ticket").GetString()
            ?? throw new WidgetException(Unreachable);

    public Uri SocketUri(Guid liveId, string ticket, int fromMs)
    {
        var b = new UriBuilder(new Uri(Server, $"api/live/{liveId}/socket"))
        {
            Scheme = Server.Scheme == "https" ? "wss" : "ws",
            Query = $"ticket={Uri.EscapeDataString(ticket)}&fromMs={fromMs}",
        };
        return b.Uri;
    }

    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        var info = await SignInInfoAsync(ct);
        var header = await signIn.HeaderAsync(info, ct);
        using var req = new HttpRequestMessage(method, path);
        req.Headers.TryAddWithoutValidation(header.Key, header.Value);
        if (body is not null) req.Content = JsonContent.Create(body, options: Json);
        HttpResponseMessage res;
        try { res = await http.SendAsync(req, ct); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested) { throw new WidgetException(Unreachable); }
        using (res)
        {
            var text = await res.Content.ReadAsStringAsync(ct);
            if (res.StatusCode == HttpStatusCode.Unauthorized)
            {
                await signIn.ForgetAsync();
                throw new SignInNeededException("Your sign-in has run out. Sign in again from the Widget's settings.");
            }
            if (!res.IsSuccessStatusCode) throw new WidgetException(ErrorOf(text) ?? "Something went wrong in VerbaFlow.");
            if (text.Length == 0) return default!;
            try { return JsonSerializer.Deserialize<T>(text, Json)!; }
            catch (JsonException) { throw new WidgetException("Something went wrong in VerbaFlow."); }
        }
    }

    /// <summary>The plain sentence VerbaFlow wrote for people, if there is one.</summary>
    private static string? ErrorOf(string text)
    {
        try
        {
            using var d = JsonDocument.Parse(text);
            return d.RootElement.ValueKind == JsonValueKind.Object && d.RootElement.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
        }
        catch (JsonException) { return null; }
    }
}
