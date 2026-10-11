using Azure;
using Azure.Communication.Email;
using VerbaFlow.Core.Domain;
using VerbaFlow.Core.Providers;
using CoreEmail = VerbaFlow.Core.Providers.EmailMessage;

namespace VerbaFlow.Infrastructure.Azure;

/// <param name="ConnectionString">From the Communication Services resource, Keys page. A secret: user-secrets or Key Vault only.</param>
/// <param name="Sender">A sender address on a domain connected to that resource, for example DoNotReply@....azurecomm.net.</param>
public sealed record AzureEmailOptions(string ConnectionString, string Sender)
{
    public bool IsConfigured => !string.IsNullOrWhiteSpace(ConnectionString) && !string.IsNullOrWhiteSpace(Sender);
}

/// <summary>Sends email through Azure Communication Services Email. See docs/azure-email-setup.md.</summary>
public sealed class AzureEmailSender(AzureEmailOptions options) : IEmailSender
{
    private readonly EmailClient client = new(options.ConnectionString);

    public string Name => "azure-communication-email";

    public async Task<string> SendAsync(CoreEmail m, CancellationToken ct)
    {
        var message = new global::Azure.Communication.Email.EmailMessage(options.Sender,
            new EmailRecipients(m.To.Select(a => new EmailAddress(a)).ToList()),
            new EmailContent(m.Subject) { PlainText = m.PlainText });
        if (m.ReplyTo is not null) message.ReplyTo.Add(new EmailAddress(m.ReplyTo));
        foreach (var a in m.Attachments)
            message.Attachments.Add(new global::Azure.Communication.Email.EmailAttachment(a.FileName, a.ContentType, BinaryData.FromBytes(a.Content)));
        try
        {
            // Started, not Completed: the service has accepted the message; delivery carries on in the background.
            var op = await client.SendAsync(WaitUntil.Started, message, ct);
            return op.Id;
        }
        catch (RequestFailedException ex)
        {
            var kind = ex.Status switch { 401 or 403 => FaultKind.Settings, 429 => FaultKind.Busy, >= 500 => FaultKind.Unavailable, _ => FaultKind.Other };
            throw new ProviderException(kind, $"Azure email returned {ex.Status} {ex.ErrorCode}: {ex.Message}");
        }
    }
}
