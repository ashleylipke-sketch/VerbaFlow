using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Extensions.Msal;

namespace VerbaFlow.Widget;

/// <summary>
/// Signing in: with the person's Microsoft work account when VerbaFlow has Microsoft sign-in, through the normal browser
/// (Microsoft's own sign-in page). The sign-in is kept protected in the person's Windows profile (Windows data
/// protection), so it is not asked again every time. On the owner's computer, while VerbaFlow allows test users and
/// nobody signed in with Microsoft, the test user from the settings is used instead. A Google or other personal account
/// is linked through the VerbaFlow website instead; that sign-in is kept in linked.dat, protected the same way.
/// </summary>
internal sealed class WindowsSignIn(Func<WidgetSettings> settings) : ISignIn
{
    public const string RedirectUri = "http://localhost";
    private static readonly string CacheFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VerbaFlow", "Widget");
    private IPublicClientApplication? _app;
    private string _appKey = "";

    private async Task<IPublicClientApplication> AppAsync(MicrosoftSignIn ms)
    {
        var key = ms.Authority + "|" + ms.ClientId;
        if (_app is not null && _appKey == key) return _app;
        var app = PublicClientApplicationBuilder.Create(ms.ClientId).WithAuthority(ms.Authority).WithRedirectUri(RedirectUri).Build();
        try
        {
            System.IO.Directory.CreateDirectory(CacheFolder);
            var helper = await MsalCacheHelper.CreateAsync(new StorageCreationPropertiesBuilder("signin.cache", CacheFolder).Build());
            helper.RegisterCache(app.UserTokenCache);
        }
        catch (Exception) { /* the sign-in is then kept only while the Widget runs */ }
        (_app, _appKey) = (app, key);
        return app;
    }

    // ---------- The organisation's own directory ----------
    // An address that is both a personal Microsoft account and the organisation's work account: VerbaFlow answers 403
    // with the organisation's Tenant ID, and from then on the Widget signs in through that directory only.
    private sealed record Directory(string Server, string Authority, string? Hint);
    private static readonly string DirectoryFile = Path.Combine(CacheFolder, "directory.json");

    private static Directory? ReadDirectory()
    {
        try { return File.Exists(DirectoryFile) ? JsonSerializer.Deserialize<Directory>(File.ReadAllText(DirectoryFile)) : null; }
        catch (Exception) { return null; }
    }

    private static void WriteDirectory(Directory? d)
    {
        try
        {
            if (d is null) { if (File.Exists(DirectoryFile)) File.Delete(DirectoryFile); return; }
            System.IO.Directory.CreateDirectory(CacheFolder);
            File.WriteAllText(DirectoryFile, JsonSerializer.Serialize(d));
        }
        catch (Exception) { /* then it lasts only until the Widget closes */ }
    }

    private Directory? _directory = ReadDirectory();

    /// <summary>Microsoft's details, through the organisation's directory once VerbaFlow has asked for that.</summary>
    private MicrosoftSignIn? MicrosoftFor(SignInInfo info) =>
        info.Microsoft is { } ms && _directory is { } d && SameServer(d.Server) ? ms with { Authority = d.Authority } : info.Microsoft;

    /// <summary>The Microsoft account signed in now, if any.</summary>
    public async Task<string?> AccountAsync(SignInInfo info)
    {
        if (MicrosoftFor(info) is not { } ms) return null;
        return (await (await AppAsync(ms)).GetAccountsAsync()).FirstOrDefault()?.Username;
    }

    // ---------- Linked through the VerbaFlow website ----------
    private sealed record Linked(string Server, string Token, DateTimeOffset Renewed);
    private static readonly string LinkedFile = Path.Combine(CacheFolder, "linked.dat");
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("VerbaFlow.Widget.linked");

    private static Linked? ReadLinked()
    {
        try
        {
            if (!File.Exists(LinkedFile)) return null;
            var bytes = ProtectedData.Unprotect(File.ReadAllBytes(LinkedFile), Entropy, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<Linked>(bytes);
        }
        catch (Exception) { return null; }
    }

    private static void WriteLinked(Linked? l)
    {
        try
        {
            if (l is null) { if (File.Exists(LinkedFile)) File.Delete(LinkedFile); return; }
            System.IO.Directory.CreateDirectory(CacheFolder);
            File.WriteAllBytes(LinkedFile, ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(l), Entropy, DataProtectionScope.CurrentUser));
        }
        catch (Exception) { /* then it lasts only until the Widget closes */ }
    }

    private Linked? _linked = ReadLinked();
    private Func<VerbaFlowClient>? _client;
    /// <summary>The client used to renew a linked sign-in (set once the tray program has one).</summary>
    public void Use(Func<VerbaFlowClient> client) => _client = client;

    /// <summary>Whether this Widget is linked to an account through the website, for the current VerbaFlow address.</summary>
    public bool IsLinked => _linked is { } l && SameServer(l.Server);
    private bool SameServer(string server) => string.Equals(server.TrimEnd('/'), settings().Server.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Links through the website: shows the code (onCode), opens the VerbaFlow page in the browser, and waits until the
    /// person agrees there.
    /// </summary>
    public async Task LinkAsync(VerbaFlowClient client, Action<string> onCode, CancellationToken ct)
    {
        var link = await client.StartLinkAsync("Windows Widget", ct);
        onCode(link.Code);
        try { Process.Start(new ProcessStartInfo(new Uri(client.Server, "#/link-widget?code=" + Uri.EscapeDataString(link.Code)).ToString()) { UseShellExecute = true }); }
        catch (Exception) { /* the person can open the page and type the code */ }
        var until = DateTimeOffset.UtcNow.AddSeconds(link.ExpiresInSeconds);
        while (DateTimeOffset.UtcNow < until)
        {
            await Task.Delay(2000, ct);
            var token = await client.CollectLinkAsync(link.Secret, ct);
            if (token is null) continue;
            _linked = new Linked(client.Server.ToString(), token, DateTimeOffset.UtcNow);
            WriteLinked(_linked);
            return;
        }
        throw new WidgetException("The code ran out before it was agreed on the website. Try again.");
    }

    public async Task<KeyValuePair<string, string>> HeaderAsync(SignInInfo info, CancellationToken ct)
    {
        if (_linked is { } l && SameServer(l.Server))
        {
            if (DateTimeOffset.UtcNow - l.Renewed > TimeSpan.FromDays(1) && _client is not null)
            {
                var fresh = await _client().RenewLinkAsync(l.Token, ct);
                if (fresh is null) { _linked = null; WriteLinked(null); throw new SignInNeededException("Your sign-in has run out. Sign in again from the Widget's settings."); }
                _linked = l = l with { Token = fresh, Renewed = DateTimeOffset.UtcNow };
                WriteLinked(l);
            }
            _usedLink = true;
            return new("Authorization", "Bearer " + l.Token);
        }
        _usedLink = false;
        if (MicrosoftFor(info) is { } ms)
        {
            _lastMicrosoft = ms;
            var app = await AppAsync(ms);
            var account = (await app.GetAccountsAsync()).FirstOrDefault();
            if (account is not null)
            {
                try
                {
                    var r = await app.AcquireTokenSilent([ms.Scope], account).ExecuteAsync(ct);
                    return new("Authorization", "Bearer " + r.AccessToken);
                }
                catch (MsalUiRequiredException) { /* signed out, or the sign-in has run out */ }
                catch (MsalServiceException) { /* Microsoft did not answer as expected */ }
                catch (MsalClientException) { }
            }
            if (!info.TestUsers) throw new SignInNeededException("Sign in first: right-click the VerbaFlow icon near the clock, then Settings, then Sign in.");
        }
        if (info.CanLink && !info.TestUsers) throw new SignInNeededException("Sign in first: right-click the VerbaFlow icon near the clock, then Settings, then Sign in.");
        if (info.TestUsers) return new("X-Dev-User", settings().TestUser);
        throw new SignInNeededException("Sign-in is not set up on this VerbaFlow yet.");
    }

    private bool _usedLink;
    private MicrosoftSignIn? _lastMicrosoft;

    public async Task<bool> RefusedAsync(string? signInAt)
    {
        if (_usedLink && _linked is not null)
        {
            // The website link belongs to an account VerbaFlow no longer accepts (for example one email address with
            // two sign-ins): forget it and try the next way to sign in once.
            _linked = null; WriteLinked(null);
            return true;
        }
        if (signInAt is null || _lastMicrosoft is not { } ms) return false;
        var authority = "https://login.microsoftonline.com/" + signInAt;
        if (string.Equals(ms.Authority.TrimEnd('/'), authority, StringComparison.OrdinalIgnoreCase)) return false;
        var app = await AppAsync(ms);
        string? hint = null;
        foreach (var a in await app.GetAccountsAsync()) { hint ??= a.Username; await app.RemoveAsync(a); }
        _directory = new Directory(settings().Server, authority, hint);
        WriteDirectory(_directory);
        throw new DirectorySwitchException("This email address signs in with your organisation's Microsoft account. Open the Widget's settings and click Sign in with Microsoft again.");
    }

    /// <summary>Opens Microsoft's sign-in page in the browser. Returns the account name.</summary>
    public async Task<string> SignInAsync(SignInInfo info, IntPtr window, CancellationToken ct)
    {
        if (MicrosoftFor(info) is not { } ms) throw new WidgetException("Microsoft sign-in is not set up on this VerbaFlow.");
        var app = await AppAsync(ms);
        try
        {
            var hint = _directory is { } d && SameServer(d.Server) ? d.Hint : null;
            var ask = app.AcquireTokenInteractive([ms.Scope]).WithUseEmbeddedWebView(false).WithParentActivityOrWindow(window);
            ask = hint is null ? ask.WithPrompt(Prompt.SelectAccount) : ask.WithLoginHint(hint);
            var r = await ask.ExecuteAsync(ct);
            // Signing in with Microsoft by hand replaces any earlier link through the website (it would be used first).
            _linked = null; WriteLinked(null);
            return r.Account.Username;
        }
        catch (MsalException) { throw new WidgetException("The Microsoft sign-in did not finish. Try again."); }
    }

    public async Task SignOutAsync(SignInInfo info)
    {
        _linked = null; WriteLinked(null);
        var ms = MicrosoftFor(info);
        _directory = null; WriteDirectory(null);
        if (ms is null) return;
        var app = await AppAsync(ms);
        foreach (var a in await app.GetAccountsAsync()) await app.RemoveAsync(a);
    }

    public Task ForgetAsync() => Task.CompletedTask; // a refused token is renewed on the next call; signing out is the person's choice
}
