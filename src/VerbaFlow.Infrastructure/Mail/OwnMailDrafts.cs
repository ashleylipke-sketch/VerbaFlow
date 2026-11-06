using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using VerbaFlow.Core.Domain;
using VerbaFlow.Core.Providers;
using VerbaFlow.Infrastructure.Export;

namespace VerbaFlow.Infrastructure.Mail;

/// <summary>
/// Drafts made with a mail permission the person's own browser got (no client secret, no administrator): Outlook.com and
/// other personal Microsoft mailboxes through Microsoft Graph, and Gmail through the Gmail API. The token is used for this
/// one request and never stored or written anywhere.
/// </summary>
public sealed class OwnMailDrafts(HttpClient http) : IOwnMailDrafts
{
    public bool Gmail { get; init; }
    public string GraphBase { get; init; } = "https://graph.microsoft.com/v1.0";
    public string GmailBase { get; init; } = "https://gmail.googleapis.com";
    /// <summary>Gmail takes a message of up to 35 MB in one upload.</summary>
    public const int GmailLimit = 35 * 1024 * 1024;

    public async Task<MailDraft> CreateAsync(string service, string mailToken, EmailMessage message, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(mailToken)) throw new ProviderException(FaultKind.Settings, "No mail permission was given by the browser.");
        if (service == MailServices.Outlook) return await GraphMailDrafts.WriteAsync(http, GraphBase, mailToken, message, ct);
        if (service == MailServices.Gmail) return await GmailAsync(mailToken, message, ct);
        throw new ArgumentException("Unknown mail service.", nameof(service));
    }

    private async Task<MailDraft> GmailAsync(string token, EmailMessage message, CancellationToken ct)
    {
        // Gmail fills in the sender itself; the message goes up as it is (multipart/mixed with the attachment).
        var eml = EmlWriter.Write(message);
        if (eml.Length > GmailLimit) throw new ProviderException(FaultKind.Other, $"The email is too large for Gmail ({eml.Length} bytes).");
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{GmailBase}/upload/gmail/v1/users/me/drafts?uploadType=media")
        {
            Content = new ByteArrayContent(eml),
        };
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("message/rfc822");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var res = await http.SendAsync(req, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
        {
            var detail = text.Length > 600 ? text[..600] : text;
            // Gmail says this when the Google account has no Gmail (a Google account made with another email address).
            if (text.Contains("failedPrecondition", StringComparison.OrdinalIgnoreCase) || text.Contains("Mail service not enabled", StringComparison.OrdinalIgnoreCase))
                throw new NoMailboxException($"Gmail found no mailbox ({(int)res.StatusCode}): {detail}");
            throw new ProviderException(res.StatusCode switch
            {
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => FaultKind.Settings,
                HttpStatusCode.TooManyRequests => FaultKind.Busy,
                >= HttpStatusCode.InternalServerError => FaultKind.Unavailable,
                _ => FaultKind.Other,
            }, $"Gmail could not make the draft ({(int)res.StatusCode}): {detail}");
        }
        var json = JsonNode.Parse(text);
        var id = json?["id"]?.GetValue<string>() ?? throw new ProviderException(FaultKind.Other, "Gmail gave no id for the draft.");
        return new MailDraft("https://mail.google.com/mail/#drafts", id);
    }
}
