using System.Security.Cryptography;
using VerbaFlow.Core.Domain;
using VerbaFlow.Infrastructure.Persistence;

namespace VerbaFlow.Infrastructure.Media;

public sealed record MediaAsset(Guid Id, Guid ItemId, string Kind, string Sha256, long SizeBytes, string FileName,
    string ContentType, string Provenance, DateTimeOffset StoredAt);

/// <summary>An upload held in quarantine until it has been scanned. Not part of the record yet.</summary>
public sealed record StagedFile(string Path, long SizeBytes, string Sha256);

/// <summary>
/// Original recordings are write-once: hashed as they arrive, stored read-only, and never modified.
/// Production equivalent: Blob Storage with an immutability policy.
/// </summary>
public sealed class FileMediaStore(string root, DocTable<MediaAsset> table, TimeProvider clock)
{
    public async Task<StagedFile> StageAsync(Stream content, long maxBytes, CancellationToken ct)
    {
        var dir = Path.Combine(root, ".quarantine");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, Guid.NewGuid().ToString("N"));
        using var sha = SHA256.Create();
        long total = 0;
        try
        {
            await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            var buffer = new byte[81920];
            int n;
            while ((n = await content.ReadAsync(buffer, ct)) > 0)
            {
                total += n;
                if (total > maxBytes) throw new Domain_SizeException(maxBytes);
                sha.TransformBlock(buffer, 0, n, null, 0);
                await file.WriteAsync(buffer.AsMemory(0, n), ct);
            }
            sha.TransformFinalBlock([], 0, 0);
        }
        catch { TryDelete(path); throw; }
        return new StagedFile(path, total, Convert.ToHexString(sha.Hash!).ToLowerInvariant());
    }

    public Stream OpenStaged(StagedFile f) => new FileStream(f.Path, FileMode.Open, FileAccess.Read, FileShare.Read);

    public void Discard(StagedFile f) => TryDelete(f.Path);

    public Task<MediaAsset> CommitOriginalAsync(StagedFile staged, Guid itemId, string fileName, string contentType, string provenance) =>
        CommitAsync(staged, itemId, fileName, contentType, provenance, "original");

    /// <summary>Keeps one save of an unfinished dictation. Each save is its own write-once file; earlier saves are never changed.</summary>
    public Task<MediaAsset> CommitDraftAsync(StagedFile staged, Guid itemId, int version, string fileName, string contentType) =>
        CommitAsync(staged, itemId, fileName, contentType, $"Unfinished dictation, save {version}", DraftKind(version));

    public static string DraftKind(int version) => $"draft:{version}";

    /// <summary>The newest save of an unfinished dictation.</summary>
    public async Task<MediaAsset?> FindLatestDraftAsync(Guid itemId) =>
        (await table.ListAsync()).Where(a => a.ItemId == itemId && a.Kind.StartsWith("draft:"))
            .OrderByDescending(a => int.TryParse(a.Kind[6..], out var n) ? n : 0).FirstOrDefault();

    private async Task<MediaAsset> CommitAsync(StagedFile staged, Guid itemId, string fileName, string contentType, string provenance, string kind)
    {
        var id = Guid.NewGuid();
        var dir = Path.Combine(root, itemId.ToString("N"));
        Directory.CreateDirectory(dir);
        var dest = Path.Combine(dir, id.ToString("N") + ".bin");
        File.Move(staged.Path, dest);
        // Belt and braces: VerbaFlow never writes to a stored file anyway. Some network shares (online storage) refuse the mark.
        try { File.SetAttributes(dest, FileAttributes.ReadOnly); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        var asset = new MediaAsset(id, itemId, kind, staged.Sha256, staged.SizeBytes, fileName, contentType, provenance, clock.GetUtcNow());
        await table.UpsertAsync(id, asset);
        return asset;
    }

    /// <summary>A changed recording of a dictation (more put in or recorded over). Earlier recordings are kept as they were.</summary>
    public Task<MediaAsset> CommitRevisionAsync(StagedFile staged, Guid itemId, int revision, string fileName, string contentType) =>
        CommitAsync(staged, itemId, fileName, contentType, $"Recording changed by the author, change {revision}", $"revision:{revision}");

    /// <summary>The recording as it is now: the newest change of it, or the original when it was never changed.</summary>
    public async Task<MediaAsset?> FindCurrentAsync(Guid itemId)
    {
        var all = (await table.ListAsync()).Where(a => a.ItemId == itemId).ToList();
        return all.Where(a => a.Kind.StartsWith("revision:")).OrderByDescending(a => int.TryParse(a.Kind[9..], out var n) ? n : 0).FirstOrDefault()
            ?? all.FirstOrDefault(a => a.Kind == "original");
    }

    public async Task<MediaAsset?> FindOriginalAsync(Guid itemId) =>
        (await table.ListAsync()).FirstOrDefault(a => a.ItemId == itemId && a.Kind == "original");

    /// <summary>Opens a stored file. FileShare.Delete lets a deletion go ahead while someone is still playing it.</summary>
    public Stream OpenRead(MediaAsset a)
    {
        var path = PathFor(a);
        if (!File.Exists(path)) throw new NotFoundException("The recording is missing.");
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
    }

    /// <summary>Recomputes the hash of the stored file and compares it with the hash recorded at capture.</summary>
    public async Task<bool> VerifyAsync(MediaAsset a)
    {
        await using var s = OpenRead(a);
        var h = Convert.ToHexString(await SHA256.HashDataAsync(s)).ToLowerInvariant();
        return h == a.Sha256;
    }

    /// <summary>
    /// Deletes the stored audio files of an item when the recording is deleted. The media records (hash, size, when stored)
    /// stay as the tombstone; only the content goes.
    /// </summary>
    /// <remarks>
    /// Never throws for a file that cannot be removed (for example one a sync tool such as OneDrive is holding): it tries
    /// again a few times, then reports it in <c>Failed</c> so the deletion itself still completes and is recorded.
    /// </remarks>
    public async Task<(int Removed, int Failed)> DeleteContentAsync(Guid itemId)
    {
        int removed = 0, failed = 0;
        foreach (var a in (await table.ListAsync()).Where(a => a.ItemId == itemId))
        {
            var path = PathFor(a);
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    if (File.Exists(path))
                    {
                        try { File.SetAttributes(path, FileAttributes.Normal); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                        File.Delete(path);
                        removed++;
                    }
                    break;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    if (attempt >= 4) { failed++; break; }
                    await Task.Delay(250 * (attempt + 1));
                }
            }
        }
        try
        {
            var dir = Path.Combine(root, itemId.ToString("N"));
            if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* an empty folder left behind is harmless */ }
        return (removed, failed);
    }

    private string PathFor(MediaAsset a) => Path.Combine(root, a.ItemId.ToString("N"), a.Id.ToString("N") + ".bin");

    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ } }
}

public sealed class Domain_SizeException(long maxBytes)
    : Exception($"The file is larger than the company limit of {maxBytes / (1024 * 1024)} MB.");
