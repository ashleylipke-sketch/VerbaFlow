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

    public async Task<string> SendAsync(CoreEmail m, CancellationToken ct) => (await StartAsync(m, ct)).Id;

    public async Task<EmailCheck> SendAndWaitAsync(CoreEmail m, CancellationToken ct)
    {
        var op = await StartAsync(m, ct);
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
        wait.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            await op.WaitForCompletionAsync(TimeSpan.FromSeconds(2), wait.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new(EmailOutcome.StillWaiting, op.Id, null); }
        catch (RequestFailedException ex) { return new(EmailOutcome.Failed, op.Id, $"Azure email returned {ex.Status} {ex.ErrorCode}: {ex.Message}"); }
        var result = op.Value;
        return result.Status == EmailSendStatus.Succeeded ? new(EmailOutcome.HandedOver, op.Id, null)
            : new(EmailOutcome.Failed, op.Id, $"Azure email finished with status {result.Status}");
    }

    private async Task<EmailSendOperation> StartAsync(CoreEmail m, CancellationToken ct)
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
            return await client.SendAsync(WaitUntil.Started, message, ct);
        }
        catch (RequestFailedException ex)
        {
            var kind = ex.Status switch { 401 or 403 => FaultKind.Settings, 429 => FaultKind.Busy, >= 500 => FaultKind.Unavailable, _ => FaultKind.Other };
            throw new ProviderException(kind, $"Azure email returned {ex.Status} {ex.ErrorCode}: {ex.Message}");
        }
    }
}
