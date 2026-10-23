using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Extensions.Msal;

namespace VerbaFlow.Widget;

/// <summary>
/// Signing in: with the person's Microsoft work account when VerbaFlow has Microsoft sign-in, through the normal browser
/// (Microsoft's own sign-in page). The sign-in is kept protected in the person's Windows profile (Windows data
/// protection), so it is not asked again every time. On the owner's computer, while VerbaFlow allows test users and
/// nobody signed in with Microsoft, the test user from the settings is used instead.
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
            Directory.CreateDirectory(CacheFolder);
            var helper = await MsalCacheHelper.CreateAsync(new StorageCreationPropertiesBuilder("signin.cache", CacheFolder).Build());
            helper.RegisterCache(app.UserTokenCache);
        }
        catch (Exception) { /* the sign-in is then kept only while the Widget runs */ }
        (_app, _appKey) = (app, key);
        return app;
    }

    /// <summary>The Microsoft account signed in now, if any.</summary>
    public async Task<string?> AccountAsync(SignInInfo info)
    {
        if (info.Microsoft is not { } ms) return null;
        return (await (await AppAsync(ms)).GetAccountsAsync()).FirstOrDefault()?.Username;
    }

    public async Task<KeyValuePair<string, string>> HeaderAsync(SignInInfo info, CancellationToken ct)
    {
        if (info.Microsoft is { } ms)
        {
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
            if (!info.TestUsers) throw new SignInNeededException("Sign in first: right-click the VerbaFlow icon near the clock, then Sign in with Microsoft.");
        }
        if (info.TestUsers) return new("X-Dev-User", settings().TestUser);
        throw new SignInNeededException("Sign-in is not set up on this VerbaFlow yet.");
    }

    /// <summary>Opens Microsoft's sign-in page in the browser. Returns the account name.</summary>
    public async Task<string> SignInAsync(SignInInfo info, IntPtr window, CancellationToken ct)
    {
        if (info.Microsoft is not { } ms) throw new WidgetException("Microsoft sign-in is not set up on this VerbaFlow.");
        var app = await AppAsync(ms);
        try
        {
            var r = await app.AcquireTokenInteractive([ms.Scope]).WithPrompt(Prompt.SelectAccount).WithUseEmbeddedWebView(false)
                .WithParentActivityOrWindow(window).ExecuteAsync(ct);
            return r.Account.Username;
        }
        catch (MsalException) { throw new WidgetException("The Microsoft sign-in did not finish. Try again."); }
    }

    public async Task SignOutAsync(SignInInfo info)
    {
        if (info.Microsoft is not { } ms) return;
        var app = await AppAsync(ms);
        foreach (var a in await app.GetAccountsAsync()) await app.RemoveAsync(a);
    }

    public Task ForgetAsync() => Task.CompletedTask; // a refused token is renewed on the next call; signing out is the person's choice
}
