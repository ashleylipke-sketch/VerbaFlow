using System.Text;
using VerbaFlow.Core.Providers;

namespace VerbaFlow.Infrastructure.Export;

/// <summary>Writes an email as a standard .eml file (MIME), with the text and attachments base64-encoded.</summary>
public static class EmlWriter
{
    /// <param name="draft">
    /// True for an unsent draft: no sender or recipients, and the "X-Unsent: 1" header, so Outlook opens it as a new email
    /// from the person's own account, ready to address and send.
    /// </param>
    public static byte[] Write(EmailMessage m, bool draft = false, string? from = null)
    {
        var boundary = "vf-" + Guid.NewGuid().ToString("N");
        var sb = new StringBuilder();
        if (draft) sb.Append("X-Unsent: 1\r\n");
        if (from is not null) sb.Append($"From: {from}\r\n");
        if (m.To.Count > 0) sb.Append($"To: {string.Join(", ", m.To)}\r\n");
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
        return Encoding.ASCII.GetBytes(sb.ToString()); // everything above is ASCII: non-ASCII text is encoded
    }

    private static string Wrap(string b64) =>
        string.Join("\r\n", Enumerable.Range(0, (b64.Length + 75) / 76).Select(i => b64.Substring(i * 76, Math.Min(76, b64.Length - i * 76))));
}
