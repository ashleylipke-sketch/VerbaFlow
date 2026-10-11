using System.Text;
using VerbaFlow.Core.Providers;

namespace VerbaFlow.Infrastructure.StandIns;

/// <summary>
/// STAND-IN email: nothing leaves the computer. Each message is saved as an .eml file in the outbox folder, which
/// Outlook or Windows Mail can open, so the whole flow can be tried before an email service is set up.
/// </summary>
public sealed class StandInEmailSender(string outbox) : IEmailSender
{
    public string Name => "stand-in-outbox";
    public string Outbox { get; } = outbox;

    public async Task<string> SendAsync(EmailMessage m, CancellationToken ct)
    {
        Directory.CreateDirectory(Outbox);
        var id = $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..8]}";
        var boundary = "vf-" + Guid.NewGuid().ToString("N");
        var sb = new StringBuilder();
        sb.Append("From: VerbaFlow <no-reply@verbaflow.local>\r\n");
        sb.Append($"To: {string.Join(", ", m.To)}\r\n");
        if (m.ReplyTo is not null) sb.Append($"Reply-To: {m.ReplyTo}\r\n");
        sb.Append($"Subject: =?utf-8?B?{Convert.ToBase64String(Encoding.UTF8.GetBytes(m.Subject))}?=\r\n");
        sb.Append($"Date: {DateTimeOffset.UtcNow:r}\r\nMIME-Version: 1.0\r\n");
        sb.Append($"Content-Type: multipart/mixed; boundary=\"{boundary}\"\r\n\r\n");
        sb.Append($"--{boundary}\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Transfer-Encoding: base64\r\n\r\n");
        sb.Append(Wrap(Convert.ToBase64String(Encoding.UTF8.GetBytes(m.PlainText)))).Append("\r\n");
        foreach (var a in m.Attachments)
        {
            // A plain fallback name, then the exact name in the standard encoded form (RFC 2231) that mail programs prefer.
            var plain = new string(a.FileName.Select(c => c is >= ' ' and < (char)127 and not '"' and not '\\' ? c : '_').ToArray());
            var exact = Uri.EscapeDataString(a.FileName);
            sb.Append($"--{boundary}\r\nContent-Type: {a.ContentType}; name=\"{plain}\"\r\n");
            sb.Append($"Content-Disposition: attachment; filename=\"{plain}\"; filename*=UTF-8''{exact}\r\nContent-Transfer-Encoding: base64\r\n\r\n");
            sb.Append(Wrap(Convert.ToBase64String(a.Content))).Append("\r\n");
        }
        sb.Append($"--{boundary}--\r\n");
        await File.WriteAllTextAsync(Path.Combine(Outbox, id + ".eml"), sb.ToString(), ct);
        return "outbox:" + id;
    }

    private static string Wrap(string b64) =>
        string.Join("\r\n", Enumerable.Range(0, (b64.Length + 75) / 76).Select(i => b64.Substring(i * 76, Math.Min(76, b64.Length - i * 76))));
}
