using System.IO.Compression;
using VerbaFlow.Core.Audit;
using VerbaFlow.Core.Domain;
using VerbaFlow.Core.Providers;
using VerbaFlow.Core.Security;
using VerbaFlow.Infrastructure.Media;

namespace VerbaFlow.Infrastructure.Services;

public sealed record AttachmentRow(Guid Id, string FileName, string ContentType, long SizeBytes, string AddedBy, DateTimeOffset AddedAt, bool CanRemove);

/// <param name="CanAdd">Whether this person can add files now (not once the recording is approved).</param>
/// <param name="Why">A plain sentence when files cannot be added or removed, or null.</param>
public sealed record AttachmentList(IReadOnlyList<AttachmentRow> Files, bool CanAdd, string? Why);

public sealed record AttachmentUpload(Stream Content, string FileName);

public sealed record AttachmentFile(Stream Content, string ContentType, string FileName);

/// <summary>
/// Files kept with a meeting or dictation (agenda, papers, slides, the letter being answered). They belong to the whole
/// recording, so a reopened version keeps them. Whoever can edit the text can add and remove them until it is approved;
/// approval locks them with the rest. Anyone who can open the recording can download them. Every file is scanned, checked
/// against its type, stored write-once and fingerprinted, and every add, download and removal is in the History.
/// Deleting the recording (by hand or by the retention rules) deletes its files too; removing only its sound keeps them.
/// </summary>
public sealed class AttachmentService(Stores stores, IMalwareScanner scanner, TimeProvider clock)
{
    private const string Product = "speak";

    public async Task<AttachmentList> ListAsync(User actor, Guid itemId)
    {
        var item = await stores.RequireItemAsync(itemId);
        PermissionEvaluator.Require(actor, item, Capability.Read);
        var why = await WhyNotAsync(actor, item);
        var users = (await stores.Users.ListAsync()).ToDictionary(u => u.Id, u => u.Name);
        var files = item.Status == ItemStatus.Purged ? [] : (await ForChainAsync(item.ChainId))
            .Select(a => new AttachmentRow(a.Id, a.FileName, a.ContentType, a.SizeBytes, users.GetValueOrDefault(a.AddedBy, "?"), a.AddedAt, why is null))
            .ToList();
        return new(files, why is null, why);
    }

    public async Task<IReadOnlyList<Attachment>> ForChainAsync(Guid chainId) =>
        (await stores.Attachments.ListAsync()).Where(a => a.ChainId == chainId).OrderBy(a => a.AddedAt).ThenBy(a => a.FileName).ToList();

    public async Task<AttachmentList> AddAsync(User actor, Guid itemId, IReadOnlyList<AttachmentUpload> uploads, CancellationToken ct = default)
    {
        var item = await stores.RequireItemAsync(itemId);
        PermissionEvaluator.Require(actor, item, Capability.Read);
        if (await WhyNotAsync(actor, item) is { } why) throw new DomainException(why);
        if (uploads.Count == 0) throw new DomainException("No file was received.");
        var existing = await ForChainAsync(item.ChainId);
        if (existing.Count + uploads.Count > Attachment.MaxPerItem)
            throw new DomainException($"A recording can have up to {Attachment.MaxPerItem} attached files. Remove one first.");
        foreach (var up in uploads) Attachment.TypeFor(SafeName(up.FileName)); // refuse the whole batch before storing any

        foreach (var up in uploads)
        {
            var name = SafeName(up.FileName);
            var type = Attachment.TypeFor(name);
            StagedFile staged;
            try { staged = await stores.Media.StageAsync(up.Content, Attachment.MaxBytes, ct); }
            catch (Domain_SizeException)
            {
                await Audit(actor, item, "upload.rejected", ("kind", "attachment"), ("fileName", name), ("reason", "too large"));
                throw new DomainException($"\"{name}\" is larger than {Attachment.MaxBytes / 1024 / 1024} MB, so it cannot be attached.");
            }
            try
            {
                if (staged.SizeBytes == 0) throw new DomainException($"\"{name}\" is empty.");
                await CheckContentAsync(staged, name);
                await using (var s = stores.Media.OpenStaged(staged))
                {
                    var scan = await scanner.ScanAsync(s, ct);
                    if (scan.Outcome != ScanOutcome.Clean)
                    {
                        await Audit(actor, item, "upload.rejected", ("kind", "attachment"), ("fileName", name), ("scan", scan.Outcome), ("detail", scan.Detail));
                        throw new DomainException(scan.Outcome == ScanOutcome.Rejected
                            ? $"\"{name}\" was rejected by the virus scan and has not been stored."
                            : $"\"{name}\" could not be scanned and has not been stored. {scan.Detail}");
                    }
                }
            }
            catch { stores.Media.Discard(staged); throw; }

            var id = Guid.NewGuid();
            var asset = await stores.Media.CommitAttachmentAsync(staged, id, name, type);
            await stores.Attachments.UpsertAsync(id, new Attachment(id, item.ChainId, item.Id, name, type, asset.SizeBytes, asset.Sha256, actor.Id, clock.GetUtcNow()));
            await Audit(actor, item, "attachment.added", ("attachment", id), ("fileName", name), ("sizeBytes", asset.SizeBytes), ("sha256", asset.Sha256));
        }
        return await ListAsync(actor, itemId);
    }

    public async Task<AttachmentList> RemoveAsync(User actor, Guid itemId, Guid attachmentId)
    {
        var item = await stores.RequireItemAsync(itemId);
        PermissionEvaluator.Require(actor, item, Capability.Read);
        var a = await RequireAsync(item, attachmentId);
        if (await WhyNotAsync(actor, item) is { } why) throw new DomainException(why);
        var (removed, failed) = await stores.Media.DeleteContentAsync(a.Id);
        await stores.Attachments.DeleteAsync(a.Id);
        await Audit(actor, item, "attachment.removed", ("attachment", a.Id), ("fileName", a.FileName), ("sha256", a.Sha256),
            ("fileNotRemoved", failed > 0 ? true : null));
        return await ListAsync(actor, itemId);
    }

    public async Task<AttachmentFile> OpenAsync(User actor, Guid itemId, Guid attachmentId)
    {
        var item = await stores.RequireItemAsync(itemId);
        PermissionEvaluator.Require(actor, item, Capability.Read);
        if (item.Status == ItemStatus.Purged) throw new NotFoundException("This recording has been deleted, with its attached files.");
        var a = await RequireAsync(item, attachmentId);
        var asset = await stores.Media.FindAttachmentAsync(a.Id) ?? throw new NotFoundException("The attached file is missing.");
        var stream = stores.Media.OpenRead(asset);
        await Audit(actor, item, "attachment.downloaded", ("attachment", a.Id), ("fileName", a.FileName), ("sha256", a.Sha256));
        return new(stream, a.ContentType, a.FileName);
    }

    /// <summary>Deletes every attached file of a recording, when the recording itself is deleted. Returns how many there were.</summary>
    public static async Task<int> DeleteForChainAsync(Stores stores, Guid chainId)
    {
        var list = (await stores.Attachments.ListAsync()).Where(a => a.ChainId == chainId).ToList();
        foreach (var a in list)
        {
            await stores.Media.DeleteContentAsync(a.Id);
            await stores.Attachments.DeleteAsync(a.Id);
        }
        return list.Count;
    }

    /// <summary>Why this person cannot add or remove files on this item now, or null when they can.</summary>
    private async Task<string?> WhyNotAsync(User actor, Item item)
    {
        if (item.Status == ItemStatus.Purged) return "This recording has been deleted.";
        if (item.IsLocked) return "The recording is approved, so its attached files are locked. Reopen it to change them.";
        if ((await stores.Items.ListAsync()).Any(i => i.SupersedesItemId == item.Id)) return "A newer version of this recording exists. Add files there.";
        return PermissionEvaluator.Evaluate(actor, item, Capability.Write).Allowed ? null : "Only someone who can edit this recording can add or remove files.";
    }

    private async Task<Attachment> RequireAsync(Item item, Guid attachmentId) =>
        await stores.Attachments.GetAsync(attachmentId) is { } a && a.ChainId == item.ChainId ? a
            : throw new NotFoundException("That attached file does not exist.");

    /// <summary>Refuses a file whose content does not match its name, and Office files that carry macros.</summary>
    private async Task CheckContentAsync(StagedFile staged, string name)
    {
        var head = new byte[8];
        int n;
        await using (var s = stores.Media.OpenStaged(staged)) n = await s.ReadAsync(head);
        if (!Attachment.LooksLike(name, head.AsSpan(0, n)))
            throw new DomainException($"\"{name}\" is not the kind of file its name says, so it has not been stored.");
        if (Path.GetExtension(name).ToLowerInvariant() is ".docx" or ".xlsx" or ".pptx")
        {
            try
            {
                await using var s = stores.Media.OpenStaged(staged);
                using var zip = new ZipArchive(s, ZipArchiveMode.Read);
                if (zip.Entries.Any(e => e.Name.Equals("vbaProject.bin", StringComparison.OrdinalIgnoreCase)))
                    throw new DomainException($"\"{name}\" contains macros, which could run code, so it cannot be attached. Save it without macros and try again.");
            }
            catch (InvalidDataException) { throw new DomainException($"\"{name}\" is damaged or not really an Office file, so it has not been stored."); }
        }
    }

    private static string SafeName(string fileName)
    {
        var n = Path.GetFileName((fileName ?? "").Replace('\\', '/')).Trim();
        n = new string(n.Where(c => !char.IsControl(c)).ToArray());
        if (n.Length == 0) n = "file";
        return n.Length > 150 ? n[..(150 - Path.GetExtension(n).Length)] + Path.GetExtension(n) : n;
    }

    private Task Audit(User actor, Item item, string type, params (string, object?)[] details) =>
        stores.Audit.AppendAsync(actor.Id, MeetingService.CapacityOf(actor, item), Product, item.Id, type, AuditChain.Details(details));
}
