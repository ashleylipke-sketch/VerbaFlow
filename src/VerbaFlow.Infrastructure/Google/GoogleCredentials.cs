using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using VerbaFlow.Core.Domain;

namespace VerbaFlow.Infrastructure.Google;

/// <summary>
/// Signs VerbaFlow in to Google Cloud from a credentials file, the way Google's own tools do ("application default
/// credentials"). Two kinds are understood:
/// - "authorized_user": a person's own Google sign-in, made on this computer with
///   <c>gcloud auth application-default login</c> (no key file, so it works where the organisation forbids keys);
/// - "service_account": a key file downloaded for a service account.
/// The file is read once; the short-lived access token is fetched when needed and kept until 5 minutes before it runs out.
/// Nothing from the file is ever logged.
/// </summary>
public sealed class GoogleCredentials
{
    private const string Scope = "https://www.googleapis.com/auth/cloud-platform";
    private readonly JsonObject file;
    private readonly SemaphoreSlim gate = new(1, 1);
    private string? token;
    private DateTimeOffset until;

    private GoogleCredentials(JsonObject file, string kind, string? project)
    {
        this.file = file;
        Kind = kind;
        Project = project;
    }

    /// <summary>"authorized_user", "service_account" or "access_token".</summary>
    public string Kind { get; }
    /// <summary>A person's sign-in rather than a service account: Google then needs to be told which project pays.</summary>
    public bool IsUser => Kind == "authorized_user";
    /// <summary>The project named in the file (quota project of a person's sign-in, or the service account's project).</summary>
    public string? Project { get; }
    /// <summary>The clock, for tests.</summary>
    public TimeProvider Clock { get; init; } = TimeProvider.System;

    /// <summary>
    /// The file named in settings, else the GOOGLE_APPLICATION_CREDENTIALS variable, else the place gcloud saves a sign-in
    /// (%APPDATA%\gcloud on Windows, ~/.config/gcloud elsewhere). Null when there is none.
    /// </summary>
    public static string? FindFile(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured)) return configured;
        var env = Environment.GetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS");
        if (!string.IsNullOrWhiteSpace(env)) return env;
        var folder = OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "gcloud")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "gcloud");
        var path = Path.Combine(folder, "application_default_credentials.json");
        return File.Exists(path) ? path : null;
    }

    /// <summary>Reads a credentials file. Throws with a plain reason (no secrets) when it cannot be used.</summary>
    public static GoogleCredentials Load(string path) => Parse(File.ReadAllText(path));

    public static GoogleCredentials Parse(string json)
    {
        JsonObject obj;
        try { obj = JsonNode.Parse(json)?.AsObject() ?? throw new FormatException(); }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException)
        { throw new InvalidOperationException("The Google credentials file is not valid JSON."); }
        var kind = obj["type"]?.GetValue<string>() ?? "";
        return kind switch
        {
            "authorized_user" when Has(obj, "client_id", "client_secret", "refresh_token") => new(obj, kind, obj["quota_project_id"]?.GetValue<string>()),
            "service_account" when Has(obj, "client_email", "private_key") => new(obj, kind, obj["project_id"]?.GetValue<string>()),
            "authorized_user" or "service_account" => throw new InvalidOperationException($"The Google credentials file ({kind}) is missing a field."),
            _ => throw new InvalidOperationException($"Google credentials of type '{kind}' are not supported; use gcloud auth application-default login or a service account key."),
        };
    }

    /// <summary>A short-lived access token handed over as it is (the GitHub check of the Google route signs in this way).
    /// It is not renewed, so it is only for runs shorter than an hour.</summary>
    public static GoogleCredentials FromAccessToken(string accessToken) =>
        new(new JsonObject(), "access_token", null) { token = accessToken, until = DateTimeOffset.MaxValue };

    private static bool Has(JsonObject o, params string[] names) => names.All(n => !string.IsNullOrEmpty(o[n]?.GetValue<string>()));

    public async Task<string> TokenAsync(HttpClient http, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (token is not null && Clock.GetUtcNow() < until) return token;
            var now = Clock.GetUtcNow();
            var tokenUri = file["token_uri"]?.GetValue<string>() ?? "https://oauth2.googleapis.com/token";
            var form = IsUser
                ? new Dictionary<string, string>
                {
                    ["grant_type"] = "refresh_token", ["client_id"] = Str("client_id"),
                    ["client_secret"] = Str("client_secret"), ["refresh_token"] = Str("refresh_token"),
                }
                : new Dictionary<string, string>
                {
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer", ["assertion"] = Assertion(tokenUri, now),
                };
            using var res = await http.PostAsync(tokenUri, new FormUrlEncodedContent(form), ct);
            var text = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode)
            {
                // Google's answer names the problem (e.g. invalid_grant: the sign-in was withdrawn) and holds no secret.
                var error = JsonNode.Parse(text)?["error"]?.ToString() ?? "";
                throw new ProviderException(FaultKind.Settings, $"Google sign-in for speech was refused ({(int)res.StatusCode} {error}).");
            }
            var answer = JsonNode.Parse(text)!;
            token = answer["access_token"]!.GetValue<string>();
            var seconds = answer["expires_in"]?.GetValue<int>() ?? 3600;
            until = now.AddSeconds(Math.Max(60, seconds - 300));
            return token;
        }
        finally { gate.Release(); }
    }

    private string Str(string name) => file[name]!.GetValue<string>();

    /// <summary>A service account proves itself with a short statement signed by its private key (RS256).</summary>
    private string Assertion(string tokenUri, DateTimeOffset now)
    {
        static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var header = B64(Encoding.UTF8.GetBytes("""{"alg":"RS256","typ":"JWT"}"""));
        var claims = B64(Encoding.UTF8.GetBytes(new JsonObject
        {
            ["iss"] = Str("client_email"), ["scope"] = Scope, ["aud"] = tokenUri,
            ["iat"] = now.ToUnixTimeSeconds(), ["exp"] = now.AddHours(1).ToUnixTimeSeconds(),
        }.ToJsonString()));
        using var rsa = RSA.Create();
        rsa.ImportFromPem(Str("private_key"));
        var signature = rsa.SignData(Encoding.ASCII.GetBytes(header + "." + claims), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return header + "." + claims + "." + B64(signature);
    }
}
