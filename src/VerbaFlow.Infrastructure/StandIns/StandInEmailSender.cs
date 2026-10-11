using VerbaFlow.Core.Providers;
using VerbaFlow.Infrastructure.Export;

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
        await File.WriteAllBytesAsync(Path.Combine(Outbox, id + ".eml"), EmlWriter.Write(m, from: "VerbaFlow <no-reply@verbaflow.local>"), ct);
        return "outbox:" + id;
    }
}
