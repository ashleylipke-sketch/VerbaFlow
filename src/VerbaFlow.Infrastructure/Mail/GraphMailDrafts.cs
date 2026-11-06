using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using VerbaFlow.Core.Domain;
using VerbaFlow.Core.Providers;
using VerbaFlow.Infrastructure.Calendars;

namespace VerbaFlow.Infrastructure.Mail;

/// <summary>
/// Puts a draft in the signed-in person's own Outlook mailbox through Microsoft Graph. VerbaFlow acts only as that
/// person: their sign-in token is exchanged for a Graph token ("on behalf of", delegated permission Mail.ReadWrite,
/// granted once by an administrator), so it can reach no one else's mailbox. Attachments up to about 3 MB go in with the
/// message; larger ones are uploaded in pieces.
/// </summary>
public sealed class GraphMailDrafts(HttpClient http, GraphOptions options, TimeProvider clock) : IMailDrafts
{
    /// <summary>Largest attachment sent inside the create request (Graph refuses requests over 4 MB, and base64 adds a third).</summary>
    public const int InlineLimit = 2_900_000;
    /// <summary>Upload pieces must be multiples of 320 KiB.</summary>
    public const int ChunkSize = 320 * 1024 * 10;
    public string GraphBase { get; init; } = "https://graph.microsoft.com/v1.0";
    public string LoginBase { get; init; } = "https://login.microsoftonline.com";
    private readonly ConcurrentDictionary<string, (string Token, DateTimeOffset Until)> tokens = new();

    public string Name => "microsoft-graph";

    public bool CanUse(User user, string? userToken) => !string.IsNullOrEmpty(user.EntraObjectId) && !string.IsNullOrEmpty(userToken);

    public async Task<MailDraft> CreateAsync(User user, string? userToken, EmailMessage message, CancellationToken ct)
    {
        if (!CanUse(user, userToken)) throw new ProviderException(FaultKind.Settings, "Outlook drafts need a Microsoft sign-in.");
        var token = await TokenAsync(userToken!, ct);
        var small = message.Attachments.Where(a => a.Content.Length <= InlineLimit).ToList();
        var large = message.Attachments.Where(a => a.Content.Length > InlineLimit).ToList();
        var body = new JsonObject
        {
            ["subject"] = message.Subject,
            ["body"] = new JsonObject { ["contentType"] = "Text", ["content"] = message.PlainText },
            ["toRecipients"] = new JsonArray(message.To.Select(t => (JsonNode)new JsonObject { ["emailAddress"] = new JsonObject { ["address"] = t } }).ToArray()),
            ["attachments"] = new JsonArray(small.Select(a => (JsonNode)new JsonObject
            {
                ["@odata.type"] = "#microsoft.graph.fileAttachment", ["name"] = a.FileName, ["contentType"] = a.ContentType,
                ["contentBytes"] = Convert.ToBase64String(a.Content),
            }).ToArray()),
        };
        var created = await SendAsync(HttpMethod.Post, $"{GraphBase}/me/messages", token, body, ct);
        var id = created["id"]?.GetValue<string>() ?? throw new ProviderException(FaultKind.Other, "Microsoft Graph gave no id for the draft.");
        foreach (var a in large) await UploadAsync(id, token, a, ct);
        return new MailDraft(created["webLink"]?.GetValue<string>(), id);
    }

    private async Task UploadAsync(string messageId, string token, EmailAttachment a, CancellationToken ct)
    {
        var session = await SendAsync(HttpMethod.Post, $"{GraphBase}/me/messages/{Uri.EscapeDataString(messageId)}/attachments/createUploadSession", token,
            new JsonObject { ["AttachmentItem"] = new JsonObject { ["attachmentType"] = "file", ["name"] = a.FileName, ["size"] = a.Content.Length, ["contentType"] = a.ContentType } }, ct);
        var url = session["uploadUrl"]?.GetValue<string>() ?? throw new ProviderException(FaultKind.Other, "Microsoft Graph gave no upload address for the attachment.");
        for (var start = 0; start < a.Content.Length; start += ChunkSize)
        {
            var length = Math.Min(ChunkSize, a.Content.Length - start);
            // The upload address carries its own permission: no Authorization header may be sent with it.
            using var req = new HttpRequestMessage(HttpMethod.Put, url) { Content = new ByteArrayContent(a.Content, start, length) };
            req.Content.Headers.ContentRange = new ContentRangeHeaderValue(start, start + length - 1, a.Content.Length);
            using var res = await http.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode) throw Fault(res.StatusCode, await res.Content.ReadAsStringAsync(ct), "upload the attachment");
        }
    }

    private async Task<JsonNode> SendAsync(HttpMethod method, string url, string token, JsonObject body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, url) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var res = await http.SendAsync(req, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode) throw Fault(res.StatusCode, text, "make the draft");
        return JsonNode.Parse(text) ?? new JsonObject();
    }

    /// <summary>A plain "no mailbox" for the answers Graph gives when the person has no Exchange Online mailbox.</summary>
    internal static Exception Fault(HttpStatusCode status, string body, string what)
    {
        var detail = body.Length > 600 ? body[..600] : body;
        string? code = null;
        try { code = JsonNode.Parse(body)?["error"]?["code"]?.GetValue<string>(); } catch (System.Text.Json.JsonException) { }
        if (code is "MailboxNotEnabledForRESTAPI" or "MailboxNotHostedInExchangeOnline" or "OrganizationFromTenantGuidNotFound" or "ErrorMailboxNotFound"
            or "ResourceNotFound" or "ErrorInvalidUser" || status == HttpStatusCode.NotFound
            // AADSTS500014 for Exchange Online: the directory has no (or a lapsed) Exchange Online subscription.
            || (body.Contains("AADSTS500014") && body.Contains("outlook.office365.com")))
            return new NoMailboxException($"Microsoft Graph found no mailbox ({(int)status} {code}): {detail}");
        return new ProviderException(status switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => FaultKind.Settings,
            HttpStatusCode.TooManyRequests => FaultKind.Busy,
            >= HttpStatusCode.InternalServerError => FaultKind.Unavailable,
            _ => FaultKind.Other,
        }, $"Microsoft Graph could not {what} ({(int)status} {code}): {detail}");
    }

    /// <summary>A Graph token for this person, exchanged from their VerbaFlow sign-in token and kept until shortly before it runs out.</summary>
    private async Task<string> TokenAsync(string userToken, CancellationToken ct)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(userToken)));
        if (tokens.TryGetValue(key, out var t) && t.Until > clock.GetUtcNow()) return t.Token;
        foreach (var old in tokens.Where(x => x.Value.Until <= clock.GetUtcNow()).Select(x => x.Key).ToList()) tokens.TryRemove(old, out _);
        using var res = await http.PostAsync($"{LoginBase}/{Uri.EscapeDataString(options.TenantId)}/oauth2/v2.0/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = options.ClientId,
                ["client_secret"] = options.ClientSecret,
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer",
                ["assertion"] = userToken,
                ["requested_token_use"] = "on_behalf_of",
                ["scope"] = "https://graph.microsoft.com/Mail.ReadWrite",
            }), ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
        {
            // Never pass on the request (it holds the secret and the person's token); Microsoft's answer names the problem (e.g. AADSTS65001: no consent).
            string err;
            try { err = JsonNode.Parse(body)?["error_description"]?.GetValue<string>() ?? $"status {(int)res.StatusCode}"; }
            catch (System.Text.Json.JsonException) { err = $"status {(int)res.StatusCode}"; }
            throw new ProviderException(FaultKind.Settings, "Microsoft sign-in refused VerbaFlow's token for Outlook drafts: " + (err.Length > 400 ? err[..400] : err));
        }
        var json = JsonNode.Parse(body)!;
        var value = json["access_token"]!.GetValue<string>();
        var seconds = json["expires_in"]?.GetValue<int>() ?? 3600;
        tokens[key] = (value, clock.GetUtcNow().AddSeconds(Math.Max(60, seconds - 300)));
        return value;
    }
}
