using VerbaFlow.Core.Domain;
using VerbaFlow.Core.Providers;
using VerbaFlow.Infrastructure.Export;

namespace VerbaFlow.Infrastructure.StandIns;

/// <summary>
/// STAND-IN Outlook drafts, for trying the page on a computer without Microsoft sign-in: each draft is saved as an
/// unsent .eml file in the drafts folder. Nothing reaches a mailbox.
/// </summary>
public sealed class StandInMailDrafts(string folder) : IMailDrafts
{
    public string Name => "stand-in-drafts";
    public string Folder { get; } = folder;
    public bool CanUse(User user, string? userToken) => true;

    public async Task<MailDraft> CreateAsync(User user, string? userToken, EmailMessage message, CancellationToken ct)
    {
        Directory.CreateDirectory(Folder);
        var id = $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..8]}";
        await File.WriteAllBytesAsync(Path.Combine(Folder, id + ".eml"), EmlWriter.Write(message, draft: true), ct);
        return new MailDraft(null, id);
    }
}
